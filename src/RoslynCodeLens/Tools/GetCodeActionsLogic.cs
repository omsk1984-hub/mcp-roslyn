using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetCodeActionsLogic
{
    public static async Task<IReadOnlyList<CodeActionInfo>> ExecuteAsync(
        LoadedSolution loaded, string filePath, int line, int column,
        int? endLine, int? endColumn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!DocumentLocator.TryFind(loaded, filePath, out var targetProject, out var targetDocument))
            return [];

        if (!loaded.Compilations.TryGetValue(targetProject.Id, out var compilation))
            return [];

        return await CodeActionRunner.GetActionsAsync(
            targetProject, targetDocument, compilation,
            line, column, endLine, endColumn, ct).ConfigureAwait(false);
    }
}
