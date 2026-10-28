using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetDiagnosticsLogic
{
    public static IReadOnlyList<DiagnosticInfo> Execute(
        LoadedSolution loaded, SymbolResolver resolver, string? project, string? severity,
        string? filePath = null, IReadOnlySet<string>? changedFiles = null)
    {
        return CollectCompilerDiagnostics(loaded, resolver, project, severity, filePath, changedFiles);
    }

    public static async Task<IReadOnlyList<DiagnosticInfo>> ExecuteAsync(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string? project,
        string? severity,
        bool includeAnalyzers,
        Security.TrustStore trustStore,
        Security.AnalyzerAllowlist allowlist,
        string? filePath = null,
        IReadOnlySet<string>? changedFiles = null,
        CancellationToken ct = default)
    {
        var results = CollectCompilerDiagnostics(loaded, resolver, project, severity, filePath, changedFiles);

        if (!includeAnalyzers)
            return results;

        var solutionPath = loaded.Solution.FilePath;
        if (solutionPath is null || !trustStore.IsTrusted(solutionPath))
        {
            throw new McpToolException(
                ToolErrorCode.SolutionNotTrusted,
                $"Solution '{solutionPath ?? "<unknown>"}' is not trusted for analyzer execution. " +
                $"Analyzer DLLs run as in-process code, so the user must explicitly authorize them. " +
                $"Ask the user, then call the 'trust_solution' tool with this path. " +
                $"To get compiler-only diagnostics, retry with includeAnalyzers=false.",
                new { solutionPath });
        }

        var minSeverity = ParseMinSeverity(severity);
        var normalizedFilePath = filePath != null ? Path.GetFullPath(filePath) : null;

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            ct.ThrowIfCancellationRequested();

            var projectName = resolver.GetProjectName(projectId);

            if (project != null &&
                !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
                continue;

            var roslynProject = loaded.Solution.GetProject(projectId);
            if (roslynProject == null)
                continue;

            var analyzerDiagnostics = await AnalyzerRunner.RunAnalyzersAsync(
                roslynProject, compilation, allowlist, ct).ConfigureAwait(false);

            foreach (var diagnostic in analyzerDiagnostics)
            {
                if (diagnostic.Severity < minSeverity)
                    continue;

                var lineSpan = diagnostic.Location.GetLineSpan();
                var file = lineSpan.Path ?? "";
                var line = lineSpan.StartLinePosition.Line + 1;
                var column = lineSpan.StartLinePosition.Character + 1;

                if (!MatchesFileFilter(file, normalizedFilePath, changedFiles))
                    continue;

                results.Add(new DiagnosticInfo(
                    diagnostic.Id,
                    diagnostic.Severity.ToString(),
                    diagnostic.GetMessage(),
                    file,
                    line,
                    projectName,
                    $"analyzer:{diagnostic.Id}",
                    column));
            }
        }

        return results;
    }

    private static DiagnosticSeverity ParseMinSeverity(string? severity)
    {
        return severity?.ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "warning" => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Warning
        };
    }

    private static List<DiagnosticInfo> CollectCompilerDiagnostics(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string? project,
        string? severity,
        string? filePath,
        IReadOnlySet<string>? changedFiles)
    {
        var minSeverity = ParseMinSeverity(severity);
        var results = new List<DiagnosticInfo>();
        var normalizedFilePath = filePath != null ? Path.GetFullPath(filePath) : null;

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = resolver.GetProjectName(projectId);

            if (project != null &&
                !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var diagnostic in compilation.GetDiagnostics())
            {
                // Respect `#pragma warning disable` and SuppressMessage attributes — a suppressed
                // diagnostic is not a real error from the user's perspective.
                if (diagnostic.IsSuppressed)
                    continue;

                if (diagnostic.Severity < minSeverity)
                    continue;

                var lineSpan = diagnostic.Location.GetLineSpan();
                var file = lineSpan.Path ?? "";
                var line = lineSpan.StartLinePosition.Line + 1;
                var column = lineSpan.StartLinePosition.Character + 1;

                if (!MatchesFileFilter(file, normalizedFilePath, changedFiles))
                    continue;

                results.Add(new DiagnosticInfo(
                    diagnostic.Id,
                    diagnostic.Severity.ToString(),
                    diagnostic.GetMessage(),
                    file,
                    line,
                    projectName,
                    "compiler",
                    column));
            }
        }

        return results;
    }

    /// <summary>
    /// Returns true if the diagnostic's file passes both the exact-path filter and the
    /// changed-files filter. A null filter means "no restriction" for that dimension.
    /// </summary>
    private static bool MatchesFileFilter(
        string file, string? normalizedFilePath, IReadOnlySet<string>? changedFiles)
    {
        if (normalizedFilePath != null &&
            !file.Equals(normalizedFilePath, StringComparison.OrdinalIgnoreCase))
            return false;

        if (changedFiles != null && !changedFiles.Contains(file))
            return false;

        return true;
    }
}
