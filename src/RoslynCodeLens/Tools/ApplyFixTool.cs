using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ApplyFixTool
{
    [McpServerTool(Name = "apply_fix"),
     Description("Apply a code fix for a specific diagnostic at a file location. " +
                 "Use get_code_fixes first to discover available fixes and their diagnosticId. " +
                 "Defaults to preview mode (returns the edits without writing the file). " +
                 "Set preview=false to apply the fix to disk. " +
                 "Requires the solution to be trusted (trust_solution) — code-fix providers run as in-process code.")]
    public static async Task<ApplyFixResult> Execute(
        MultiSolutionManager manager,
        Security.TrustStore trustStore,
        Security.AnalyzerAllowlist allowlist,
        [Description("Full path to the source file with the diagnostic")] string filePath,
        [Description("Line number where the diagnostic occurs (1-based)")] int line,
        [Description("Diagnostic ID to fix (e.g., 'CS0246', 'IDE0005')")] string diagnosticId,
        [Description("Column number (1-based); when omitted, the first diagnostic on the line is used")] int? column = null,
        [Description("Index of the fix to apply when several are available (default: 0)")] int fixIndex = 0,
        [Description("Preview only — return the edits without writing to disk (default: true)")] bool preview = true,
        [Description("Apply even when the solution loaded degraded (default: false)")] bool force = false,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return await ApplyFixLogic.ExecuteAsync(
            context.Loaded, filePath, line, column,
            diagnosticId, fixIndex, preview, force,
            trustStore, allowlist,
            // After a successful apply, push the written texts into the in-memory snapshot so
            // follow-up queries see them immediately (no watcher-debounce stale window).
            manager.CommitDocumentTextsAsync, ct).ConfigureAwait(false);
    }
}
