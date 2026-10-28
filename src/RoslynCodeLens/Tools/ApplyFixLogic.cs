using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

/// <summary>
/// Applies a code fix for a specific diagnostic (by id + file + line) through the shared
/// write gate. Discovers the fix via the built-in code-fix providers
/// (<see cref="CodeActionRunner.GetCodeFixActionsAsync"/>) — the same provider set
/// <c>get_code_actions</c> uses — so compiler fixes (e.g. the CS0246 "using" fix) and
/// analyzer fixes are both reachable. Trust is required exactly as in
/// <see cref="GetCodeFixesLogic"/> (the error message is copied word for word).
/// </summary>
public static class ApplyFixLogic
{
    public static async Task<ApplyFixResult> ExecuteAsync(
        LoadedSolution loaded,
        string filePath, int line, int? column,
        string diagnosticId, int fixIndex, bool preview, bool force,
        Security.TrustStore trustStore, Security.AnalyzerAllowlist allowlist,
        CommitWrittenDocuments? commitToMemory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!DocumentLocator.TryFind(loaded, filePath, out var targetProject, out var targetDocument))
        {
            throw new McpToolException(ToolErrorCode.FileNotFound,
                $"File not found in solution: {filePath}", new { filePath });
        }

        if (!loaded.Compilations.TryGetValue(targetProject.Id, out var compilation))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"No compilation available for project: {targetProject.Name}",
                new { targetProject.Name });
        }

        // Trust gate — same requirement and message as get_code_fixes (word for word),
        // because apply_fix loads the same code-fix providers as in-process code.
        var solutionPath = loaded.Solution.FilePath;
        if (solutionPath is null || !trustStore.IsTrusted(solutionPath))
        {
            throw new McpToolException(
                ToolErrorCode.SolutionNotTrusted,
                $"Solution '{solutionPath ?? "<unknown>"}' is not trusted for analyzer execution. " +
                $"'get_code_fixes' loads analyzer DLLs to discover available fixes — these run as in-process code. " +
                $"Ask the user, then call the 'trust_solution' tool with this path.",
                new { solutionPath });
        }

        var normalizedPath = Path.GetFullPath(filePath);

        // Find the target diagnostic: compiler diagnostics plus analyzer diagnostics
        // (the same union get_code_fixes inspects), filtered by id + file + line.
        var allDiagnostics = compilation.GetDiagnostics()
            .Concat(await AnalyzerRunner.RunAnalyzersAsync(targetProject, compilation, allowlist, ct).ConfigureAwait(false));

        var matchingDiagnostics = allDiagnostics
            .Where(d => string.Equals(d.Id, diagnosticId, StringComparison.Ordinal) &&
                        d.Location.IsInSource &&
                        d.Location.SourceTree?.FilePath != null &&
                        d.Location.SourceTree.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                        d.Location.GetLineSpan().StartLinePosition.Line + 1 == line)
            .ToList();

        if (matchingDiagnostics.Count == 0)
        {
            return new ApplyFixResult(false, diagnosticId, "", Applied: false, [], [],
                $"No {diagnosticId} diagnostic found at {filePath}:{line} — nothing to fix. " +
                "Run get_diagnostics to see the diagnostics in this file.");
        }

        // When a column is given, prefer the diagnostic at that column; otherwise the first.
        var diagnostic = column is { } col
            ? matchingDiagnostics.FirstOrDefault(d =>
                d.Location.GetLineSpan().StartLinePosition.Character + 1 == col)
              ?? matchingDiagnostics[0]
            : matchingDiagnostics[0];

        // Collect the actual CodeActions for this diagnostic from the built-in providers.
        var actions = await CodeActionRunner.GetCodeFixActionsAsync(
            targetProject, targetDocument, compilation, diagnostic, ct).ConfigureAwait(false);

        if (actions.Count == 0)
        {
            return new ApplyFixResult(false, diagnosticId, "", Applied: false, [], [],
                $"No code fix available for {diagnosticId} at {filePath}:{line}.");
        }

        if (fixIndex < 0 || fixIndex >= actions.Count)
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"fixIndex {fixIndex} is out of range: {actions.Count} fix(es) available for " +
                $"{diagnosticId} at {filePath}:{line} (valid range 0..{actions.Count - 1}).",
                new { fixIndex, available = actions.Count });
        }

        var action = actions[fixIndex];

        var outcome = await CodeActionRunner.ApplyOperationsAsync(
            loaded, targetProject, action, preview, force,
            commitToMemory, ct).ConfigureAwait(false);

        return new ApplyFixResult(
            outcome.Success, diagnosticId, action.Title, outcome.Applied,
            outcome.Edits, outcome.Conflicts, outcome.Message);
    }
}
