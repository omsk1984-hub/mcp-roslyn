using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class ApplyCodeActionLogic
{
    public static async Task<CodeActionResult> ExecuteAsync(
        LoadedSolution loaded, string filePath, int line, int column,
        int? endLine, int? endColumn,
        string actionTitle, bool preview,
        CommitWrittenDocuments? commitToMemory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!DocumentLocator.TryFind(loaded, filePath, out var targetProject, out var targetDocument))
        {
            return new CodeActionResult(
                Success: false,
                Title: actionTitle,
                Edits: [],
                ErrorMessage: $"File not found in solution: {filePath}");
        }

        if (!loaded.Compilations.TryGetValue(targetProject.Id, out var compilation))
        {
            return new CodeActionResult(
                Success: false,
                Title: actionTitle,
                Edits: [],
                ErrorMessage: $"No compilation available for project: {targetProject.Name}");
        }

        return await CodeActionRunner.ApplyActionAsync(
            targetProject, targetDocument, compilation,
            line, column, endLine, endColumn,
            actionTitle, preview, commitToMemory, ct).ConfigureAwait(false);
    }
}
