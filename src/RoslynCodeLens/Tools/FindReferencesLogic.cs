using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class FindReferencesLogic
{
    /// <summary>
    /// Stage 4/B: async-first entry point. <see cref="SymbolFinder.FindReferencesAsync"/> and the
    /// lazily built semantic model are awaited rather than parked on, so a solution-wide scan no
    /// longer occupies the calling thread while Roslyn walks the compilation graph.
    /// </summary>
    public static async Task<IReadOnlyList<SymbolReference>> ExecuteAsync(
        LoadedSolution loaded,
        SymbolResolver source,
        MetadataSymbolResolver metadata,
        string symbol,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var targets = source.FindSymbols(symbol);
        if (targets.Count == 0)
        {
            var resolved = metadata.Resolve(symbol);
            if (resolved == null)
                return [];
            targets = [resolved.Symbol];
        }

        return await ScanForReferencesAsync(loaded, source, targets, ct).ConfigureAwait(false);
    }

    private static async Task<List<SymbolReference>> ScanForReferencesAsync(
        LoadedSolution loaded,
        SymbolResolver resolver,
        IReadOnlyList<ISymbol> targets,
        CancellationToken ct)
    {
        var results = new List<SymbolReference>();
        var seen = new HashSet<(string File, int Line, int Column)>();
        // Semantic models are expensive, and only the rare COM-interop argument needs one.
        // Built on demand (see GetModelAsync) and reused per document for the rest of the scan.
        var models = new Dictionary<DocumentId, SemanticModel>();

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            // Roslyn's SymbolFinder handles cross-compilation symbol identity, generic
            // constructions, partial-class merges, and metadata-vs-source symbol unification.
            // A hand-rolled walk that compares with SymbolEqualityComparer.Default misses
            // references in downstream projects when the consuming compilation observes the
            // same logical symbol as a distinct ISymbol instance.
            var references = await SymbolFinder.FindReferencesAsync(target, loaded.Solution, ct)
                .ConfigureAwait(false);

            foreach (var referencedSymbol in references)
            {
                foreach (var location in referencedSymbol.Locations)
                {
                    var sourceTree = location.Location.SourceTree;
                    if (sourceTree == null)
                        continue;

                    var lineSpan = location.Location.GetLineSpan();
                    var file = lineSpan.Path;
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var column = lineSpan.StartLinePosition.Character + 1;

                    if (!seen.Add((file, line, column)))
                        continue;

                    // getInnermostNodeForTie: a base-type entry such as `class Bar : Foo` has the
                    // same span as its type name, and the default outermost pick would hand the
                    // classifier a node with no SimpleName to read.
                    var node = sourceTree.GetRoot()
                        .FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);

                    // The model is needed only by the argument branch (COM interop omitting an
                    // explicit `out`/`ref`), so it is awaited up front for argument nodes alone —
                    // the classifier keeps its synchronous callback and a solution-wide scan still
                    // builds no model for the vast majority of documents it touches.
                    SemanticModel? model = null;
                    if (node.FirstAncestorOrSelf<SimpleNameSyntax>()?.Parent is ArgumentSyntax)
                        model = await GetModelAsync(models, location.Document, ct).ConfigureAwait(false);

                    var kind = ReferenceClassifier.Classify(
                        node, referencedSymbol.Definition, () => model);
                    var snippet = ExceptionQueries.StatementSnippet(node);
                    var projectName = resolver.GetProjectName(location.Document.Project.Id);

                    results.Add(new SymbolReference(
                        kind, file, line, column, snippet, projectName, resolver.IsGenerated(file),
                        resolver.GetMarkupSource(file)));
                }
            }
        }

        return results;
    }

    private static async Task<SemanticModel?> GetModelAsync(
        Dictionary<DocumentId, SemanticModel> cache, Document document, CancellationToken ct)
    {
        if (cache.TryGetValue(document.Id, out var cached))
            return cached;

        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (model != null)
            cache[document.Id] = model;
        return model;
    }
}
