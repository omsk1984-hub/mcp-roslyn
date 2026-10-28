using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Rename;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

/// <summary>
/// Thin facade over Roslyn's own <c>ExtractMethodCodeRefactoringProvider</c>: it locates the
/// extract-method action for a line range, lets Roslyn compute the extraction (signature,
/// captured locals, return type), then renames the generated method to the requested name and
/// routes the combined change through the shared write gate
/// (<see cref="SolutionChangeSafety.PreviewOrApplyAsync"/>) — so preview/apply, conflict
/// detection, freshness and in-memory commit behave exactly like rename_symbol / apply_fix.
/// </summary>
/// <remarks>
/// The action is identified by <see cref="CodeAction.EquivalenceKey"/>, never by its title:
/// titles are localized (a ru-RU Roslyn returns «Извлечь метод»), the key is not. Probing this
/// Roslyn version showed the action is a plain <c>DocumentChangeAction</c> with no nested
/// actions and is not a <see cref="CodeActionWithOptions"/>, so the provider picks the name
/// itself (<c>NewMethod</c>, collision-free by appending digits) — hence the rename step.
/// </remarks>
public static class ExtractMethodLogic
{
    /// <summary>Locale-independent identity of Roslyn's extract-method refactoring.</summary>
    internal const string ExtractMethodEquivalenceKey = "Extract_method";

    /// <summary>Identifier Roslyn generates for an extracted method (not localized).</summary>
    internal const string GeneratedNamePrefix = "NewMethod";

    private static readonly SymbolDisplayFormat SignatureFormat =
        SymbolDisplayFormat.CSharpErrorMessageFormat
            .WithMemberOptions(
                SymbolDisplayMemberOptions.IncludeAccessibility |
                SymbolDisplayMemberOptions.IncludeModifiers |
                SymbolDisplayMemberOptions.IncludeType |
                SymbolDisplayMemberOptions.IncludeParameters)
            .WithParameterOptions(
                SymbolDisplayParameterOptions.IncludeType |
                SymbolDisplayParameterOptions.IncludeName |
                SymbolDisplayParameterOptions.IncludeParamsRefOut |
                SymbolDisplayParameterOptions.IncludeDefaultValue)
            .WithMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static async Task<ExtractMethodResult> ExecuteAsync(
        LoadedSolution loaded, string filePath,
        int startLine, int endLine, string methodName,
        bool preview, bool force,
        CommitWrittenDocuments? commitToMemory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!SyntaxFacts.IsValidIdentifier(methodName))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"'{methodName}' is not a valid C# identifier — methodName must be a bare " +
                "identifier (e.g. 'CalculateTotal'), not a signature or a quoted string.",
                new { methodName });
        }

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

        var originalText = await document.GetTextAsync(ct).ConfigureAwait(false);
        if (startLine < 1 || endLine < startLine || endLine > originalText.Lines.Count)
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"Invalid range [{startLine}, {endLine}]: lines are 1-based, startLine must be " +
                $"<= endLine, and endLine must not exceed {originalText.Lines.Count} (total lines).",
                new { startLine, endLine, totalLines = originalText.Lines.Count });
        }

        // The selection is the full content of the requested lines: column 1 .. end of endLine.
        // CreateSpan clamps the end column to the line length, so int.MaxValue means "to EOL".
        var actions = await CodeActionRunner.GetRawActionsAsync(
            project, document, compilation,
            startLine, column: 1, endLine, endColumn: int.MaxValue, ct).ConfigureAwait(false);

        var extractAction = actions.FirstOrDefault(
            a => a.EquivalenceKey == ExtractMethodEquivalenceKey);

        if (extractAction is null)
        {
            var available = actions.Count == 0
                ? "no actions at all"
                : string.Join("; ", actions.Select(a => $"{a.Title} [{a.EquivalenceKey ?? "no-key"}]"));
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"Extract-method is not available for lines {startLine}-{endLine} of {filePath} " +
                $"(the range must lie inside a single method body). Actions offered there: {available}.",
                new { filePath, startLine, endLine, available = actions.Select(a => a.Title).ToList() });
        }

        var operations = await extractAction.GetOperationsAsync(ct).ConfigureAwait(false);
        var applyOp = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
        if (applyOp is null)
        {
            return new ExtractMethodResult(false, methodName, "", Applied: false, [], [],
                $"Roslyn's '{extractAction.Title}' action produced no applicable changes.");
        }

        var extracted = applyOp.ChangedSolution;

        // Roslyn named the new method itself; move it to the requested name so the tool's
        // contract ("methodName") is actually honoured instead of silently ignored.
        var found = await FindExtractedMethodAsync(extracted, project, document, ct)
            .ConfigureAwait(false);
        if (found is null)
        {
            throw new McpToolException(ToolErrorCode.Internal,
                "Extraction succeeded but the new method could not be located in the changed " +
                "solution, so the requested name could not be applied. No files were written.",
                new { filePath, startLine, endLine, methodName });
        }

        var generatedSymbol = found.Value.Symbol;

        var finalSolution = extracted;
        if (!string.Equals(generatedSymbol.Name, methodName, StringComparison.Ordinal))
        {
            finalSolution = await Renamer.RenameSymbolAsync(
                extracted, generatedSymbol,
                new SymbolRenameOptions(
                    RenameOverloads: false, RenameInStrings: false,
                    RenameInComments: false, RenameFile: false),
                methodName, ct).ConfigureAwait(false);
        }

        var signature = await FormatSignatureAsync(finalSolution, document, methodName, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(signature))
            signature = generatedSymbol.ToDisplayString(SignatureFormat);

        // Degraded guard: a load with dropped references makes the conflict scan unreliable,
        // and the extraction itself is computed from those same broken bindings.
        var degradedRefusal = preview
            ? null
            : SolutionChangeSafety.DegradedApplyRefusal(loaded, force, "method extraction");
        if (degradedRefusal != null)
        {
            return new ExtractMethodResult(false, methodName, signature, Applied: false, [], [],
                degradedRefusal);
        }

        var outcome = await SolutionChangeSafety.PreviewOrApplyAsync(
            loaded, finalSolution, "method extraction", preview, force,
            _ => $"Extracted lines {startLine}-{endLine} of {filePath} into method '{methodName}'.",
            commitToMemory, ct).ConfigureAwait(false);

        return new ExtractMethodResult(
            outcome.Success, methodName, signature, outcome.Applied,
            outcome.Edits, outcome.Conflicts, outcome.Message);
    }

    /// <summary>
    /// Finds the method declaration the extraction added: the one whose name did not exist in
    /// the document before. Roslyn guarantees a fresh name (it suffixes digits on collision),
    /// so the set difference is exact; the <c>NewMethod</c> prefix only breaks ties when a
    /// rename of an unrelated symbol somehow landed in the same solution.
    /// </summary>
    private static async Task<(IMethodSymbol Symbol, MethodDeclarationSyntax Node)?>
        FindExtractedMethodAsync(Solution solution, Project project, Document originalDocument, CancellationToken ct)
    {
        var originalRoot = await originalDocument.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var changedDocument = solution.GetDocument(originalDocument.Id);
        if (originalRoot is null || changedDocument is null)
            return null;

        var changedRoot = await changedDocument.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (changedRoot is null)
            return null;

        var changedProject = solution.GetProject(project.Id);
        if (changedProject is null)
            return null;

        var semanticModel = await changedDocument.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (semanticModel is null)
            return null;

        var beforeNames = originalRoot.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Select(m => m.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);

        var candidates = changedRoot.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => !beforeNames.Contains(m.Identifier.Text))
            .ToList();

        if (candidates.Count == 0)
            return null;

        var node = candidates.FirstOrDefault(m =>
                       m.Identifier.Text.StartsWith(GeneratedNamePrefix, StringComparison.Ordinal))
                   ?? candidates[0];

        return semanticModel.GetDeclaredSymbol(node, ct) is IMethodSymbol symbol
            ? (symbol, node)
            : null;
    }

    /// <summary>
    /// Signature of the extracted method after the rename, resolved in the final solution so the
    /// reported name matches what lands on disk.
    /// </summary>
    private static async Task<string> FormatSignatureAsync(
        Solution solution, Document originalDocument, string methodName, CancellationToken ct)
    {
        var changedDocument = solution.GetDocument(originalDocument.Id);
        if (changedDocument is null)
            return "";

        var root = await changedDocument.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await changedDocument.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || model is null)
            return "";

        var node = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (node is null)
            return "";

        return model.GetDeclaredSymbol(node, ct) is IMethodSymbol symbol
            ? symbol.ToDisplayString(SignatureFormat)
            : "";
    }
}
