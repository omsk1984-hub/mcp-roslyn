using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ExtractMethodTool
{
    [McpServerTool(Name = "extract_method"),
     Description("Extract a contiguous range of statements inside a method into a new private method. " +
                 "Thin facade over Roslyn's own ExtractMethodCodeRefactoringProvider: Roslyn infers the " +
                 "parameters from captured locals and the return type from the extracted flow. " +
                 "Defaults to preview mode (returns the edits without writing the file). " +
                 "Set preview=false to apply changes to disk. " +
                 "New compiler errors the extraction would introduce are reported as Conflicts, " +
                 "and apply mode refuses to write them unless force=true.")]
    public static async Task<ExtractMethodResult> Execute(
        MultiSolutionManager manager,
        [Description("Full path to the C# source file")] string filePath,
        [Description("First line of the range to extract (1-based, inclusive)")] int startLine,
        [Description("Last line of the range to extract (1-based, inclusive)")] int endLine,
        [Description("Name of the new method — a bare valid C# identifier, e.g. 'CalculateTotal'")] string methodName,
        [Description("Preview only — return the edits without writing to disk (default: true)")] bool preview = true,
        [Description("Apply even when Conflicts or a degraded load are reported (default: false)")] bool force = false,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        return await ExtractMethodLogic.ExecuteAsync(
            manager.GetLoadedSolution(), filePath,
            startLine, endLine, methodName, preview, force,
            // After a successful apply, push the written texts into the in-memory snapshot so
            // follow-up queries see the new method immediately (no watcher-debounce stale window).
            manager.CommitDocumentTextsAsync, ct).ConfigureAwait(false);
    }
}
