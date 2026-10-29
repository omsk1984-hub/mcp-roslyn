using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
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

        var (targetProject, targetDocument, compilation) = ResolveTarget(loaded, filePath);
        EnsureTrusted(loaded, trustStore);

        var matchingDiagnostics = await FindMatchingDiagnosticsAsync(
            targetProject, compilation, allowlist, filePath, line, diagnosticId, ct).ConfigureAwait(false);

        if (matchingDiagnostics.Count == 0)
        {
            return new ApplyFixResult(false, diagnosticId, "", Applied: false, [], [],
                $"No {diagnosticId} diagnostic found at {filePath}:{line} — nothing to fix. " +
                "Run get_diagnostics to see the diagnostics in this file.");
        }

        var diagnostic = PickDiagnostic(matchingDiagnostics, column);

        // Collect the actual CodeActions for this diagnostic from the built-in providers.
        var actions = await CodeActionRunner.GetCodeFixActionsAsync(
            targetProject, targetDocument, compilation, diagnostic, ct).ConfigureAwait(false);

        if (actions.Count == 0)
        {
            return new ApplyFixResult(false, diagnosticId, "", Applied: false, [], [],
                $"No code fix available for {diagnosticId} at {filePath}:{line}.");
        }

        var action = SelectAction(actions, fixIndex, diagnosticId, filePath, line);

        var outcome = await CodeActionRunner.ApplyOperationsAsync(
            loaded, targetProject, action, preview, force,
            commitToMemory, ct).ConfigureAwait(false);

        return new ApplyFixResult(
            outcome.Success, diagnosticId, action.Title, outcome.Applied,
            outcome.Edits, outcome.Conflicts, outcome.Message);
    }

    /// <summary>The document, its project, and that project's compilation — the precondition for everything else.</summary>
    private static (Project Project, Document Document, Compilation Compilation) ResolveTarget(
        LoadedSolution loaded, string filePath)
    {
        if (!DocumentLocator.TryFind(loaded, filePath, out var project, out var document))
        {
            throw new McpToolException(ToolErrorCode.FileNotFound,
                $"File not found in solution: {filePath}", new { filePath });
        }

        if (!loaded.Compilations.TryGetValue(project.Id, out var compilation))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"No compilation available for project: {project.Name}",
                new { project.Name });
        }

        return (project, document, compilation);
    }

    /// <summary>
    /// Trust gate — same requirement and message as get_code_fixes (word for word), because
    /// apply_fix loads the same code-fix providers as in-process code.
    /// </summary>
    private static void EnsureTrusted(LoadedSolution loaded, Security.TrustStore trustStore)
    {
        var solutionPath = loaded.Solution.FilePath;
        if (solutionPath is not null && trustStore.IsTrusted(solutionPath))
            return;

        throw new McpToolException(
            ToolErrorCode.SolutionNotTrusted,
            $"Solution '{solutionPath ?? "<unknown>"}' is not trusted for analyzer execution. " +
            $"'get_code_fixes' loads analyzer DLLs to discover available fixes — these run as in-process code. " +
            $"Ask the user, then call the 'trust_solution' tool with this path.",
            new { solutionPath });
    }

    /// <summary>
    /// The target diagnostic: compiler diagnostics plus analyzer diagnostics (the same union
    /// get_code_fixes inspects), filtered by id + file + line.
    /// </summary>
    private static async Task<List<Diagnostic>> FindMatchingDiagnosticsAsync(
        Project project,
        Compilation compilation,
        Security.AnalyzerAllowlist allowlist,
        string filePath,
        int line,
        string diagnosticId,
        CancellationToken ct)
    {
        var analyzerDiagnostics = await AnalyzerRunner
            .RunAnalyzersAsync(project, compilation, allowlist, ct)
            .ConfigureAwait(false);

        return compilation.GetDiagnostics()
            .Concat(analyzerDiagnostics)
            .Where(d => MatchesLocation(d, diagnosticId, filePath, line))
            .ToList();
    }

    private static bool MatchesLocation(Diagnostic d, string diagnosticId, string filePath, int line)
    {
        // Comparing against the normalised path keeps `/` and `\` spellings equivalent.
        var normalizedPath = Path.GetFullPath(filePath);

        return string.Equals(d.Id, diagnosticId, StringComparison.Ordinal)
            && d.Location.IsInSource
            && d.Location.SourceTree?.FilePath is { } treePath
            && treePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)
            && d.Location.GetLineSpan().StartLinePosition.Line + 1 == line;
    }

    /// <summary>When a column is given, prefer the diagnostic at that column; otherwise the first.</summary>
    private static Diagnostic PickDiagnostic(List<Diagnostic> matching, int? column)
    {
        if (column is not { } col)
            return matching[0];

        return matching.FirstOrDefault(d =>
                   d.Location.GetLineSpan().StartLinePosition.Character + 1 == col)
               ?? matching[0];
    }

    private static CodeAction SelectAction(
        IReadOnlyList<CodeAction> actions, int fixIndex, string diagnosticId, string filePath, int line)
    {
        if (fixIndex >= 0 && fixIndex < actions.Count)
            return actions[fixIndex];

        throw new McpToolException(ToolErrorCode.InvalidArgument,
            $"fixIndex {fixIndex} is out of range: {actions.Count} fix(es) available for " +
            $"{diagnosticId} at {filePath}:{line} (valid range 0..{actions.Count - 1}).",
            new { fixIndex, available = actions.Count });
    }
}
