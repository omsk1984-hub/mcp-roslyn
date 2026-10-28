using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

/// <summary>
/// Formats a C# document (whole file or a line range) using the real workspace's
/// <see cref="Workspace.Options"/> so the project's <c>.editorconfig</c> is honored,
/// then routes the result through the shared write gate
/// (<see cref="SolutionChangeSafety.PreviewOrApplyAsync"/>) so preview/apply,
/// conflict detection, freshness, and in-memory commit behave exactly like
/// rename_symbol / change_signature.
/// </summary>
public static class FormatDocumentLogic
{
    public static async Task<FormatDocumentResult> ExecuteAsync(
        LoadedSolution loaded, string filePath,
        int[]? lineRange, bool preview, bool force,
        CommitWrittenDocuments? commitToMemory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!DocumentLocator.TryFind(loaded, filePath, out var project, out var document))
        {
            throw new McpToolException(ToolErrorCode.FileNotFound,
                $"File not found in solution: {filePath}", new { filePath });
        }

        var originalText = await document.GetTextAsync(ct).ConfigureAwait(false);

        // Compute the span to format: the whole document, or the requested line range.
        TextSpan? span = null;
        if (lineRange is not null)
        {
            if (lineRange.Length != 2)
            {
                throw new McpToolException(ToolErrorCode.InvalidArgument,
                    "lineRange must be an array of exactly two integers: [startLine, endLine].",
                    new { lineRange });
            }

            var startLine = lineRange[0];
            var endLine = lineRange[1];
            if (startLine < 1 || endLine < startLine || endLine > originalText.Lines.Count)
            {
                throw new McpToolException(ToolErrorCode.InvalidArgument,
                    $"Invalid lineRange [{startLine}, {endLine}]: must be 1-based, " +
                    $"startLine <= endLine, and endLine <= {originalText.Lines.Count} (total lines).",
                    new { lineRange });
            }

            var startOffset = originalText.Lines[startLine - 1].Start;
            var endOffset = originalText.Lines[endLine - 1].End;
            span = TextSpan.FromBounds(startOffset, endOffset);
        }

        // Use the real workspace's options (not an AdhocWorkspace default) so the
        // project's .editorconfig is honored — MSBuildWorkspace pulls .editorconfig
        // in when it opens the project.
        var options = document.Project.Solution.Workspace.Options;

        var formattedDoc = span is { } s
            ? await Formatter.FormatAsync(document, s, options, ct).ConfigureAwait(false)
            : await Formatter.FormatAsync(document, options, ct).ConfigureAwait(false);
        var formattedText = await formattedDoc.GetTextAsync(ct).ConfigureAwait(false);

        // No-op fast path: if formatting changed nothing, don't write the file (and don't
        // touch mtime — otherwise the file watcher would needlessly rebuild the project).
        if (string.Equals(formattedText.ToString(), originalText.ToString(), StringComparison.Ordinal))
        {
            return new FormatDocumentResult(true, filePath, Applied: false, [], [], "Already formatted.");
        }

        var changedSolution = loaded.Solution.WithDocumentText(document.Id, formattedText);

        // Degraded guard: a load with dropped references can make the conflict scan
        // unreliable; refuse to write in that state unless forced (previews warn).
        var degradedRefusal = preview
            ? null
            : SolutionChangeSafety.DegradedApplyRefusal(loaded, force, "formatting");
        if (degradedRefusal != null)
        {
            return new FormatDocumentResult(false, filePath, Applied: false, [], [], degradedRefusal);
        }

        var outcome = await SolutionChangeSafety.PreviewOrApplyAsync(
            loaded, changedSolution, "formatting", preview, force,
            _ => $"Formatted {filePath}.",
            commitToMemory, ct).ConfigureAwait(false);

        return new FormatDocumentResult(
            outcome.Success, filePath, outcome.Applied,
            outcome.Edits, outcome.Conflicts, outcome.Message);
    }
}
