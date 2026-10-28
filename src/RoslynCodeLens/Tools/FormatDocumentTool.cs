using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FormatDocumentTool
{
    [McpServerTool(Name = "format_document"),
     Description("Format a C# file (or a line range) using the project's .editorconfig. " +
                 "Defaults to preview mode (returns the edits without writing the file). " +
                 "Set preview=false to write the formatted text to disk. " +
                 "If the file is already formatted, returns success with applied=false and no edits. " +
                 "Use only for .cs files inside the loaded solution.")]
    public static async Task<FormatDocumentResult> Execute(
        MultiSolutionManager manager,
        [Description("Full path to the C# source file to format")] string filePath,
        [Description("Preview only — return the edits without writing to disk (default: true)")] bool preview = true,
        [Description("Optional line range to format: [startLine, endLine], 1-based inclusive. Omit to format the whole file.")]
            int[]? lineRange = null,
        [Description("Apply even when the solution loaded degraded (default: false)")] bool force = false,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        return await FormatDocumentLogic.ExecuteAsync(
            manager.GetLoadedSolution(), filePath,
            lineRange, preview, force,
            // After a successful apply, push the written texts into the in-memory snapshot so
            // follow-up queries see them immediately (no watcher-debounce stale window).
            manager.CommitDocumentTextsAsync, ct).ConfigureAwait(false);
    }
}
