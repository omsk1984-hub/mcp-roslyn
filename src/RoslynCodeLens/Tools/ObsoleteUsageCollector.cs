using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens.Models;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

/// <summary>
/// Finds every usage site of every <c>[Obsolete]</c>-marked symbol in a solution.
/// </summary>
/// <remarks>
/// Split out of <see cref="FindObsoleteUsageLogic.Execute"/> so that walking the solution and
/// assembling the report are separate concerns: this type knows about syntax trees, spans and
/// attribute data; the caller knows about filtering, sorting and counts.
/// </remarks>
internal static class ObsoleteUsageCollector
{
    /// <summary>
    /// Collects usages for every obsolete symbol declared anywhere in the solution, including
    /// symbols declared in test projects.
    /// </summary>
    /// <remarks>
    /// Test-project filtering happens on the <em>usage site</em> side: an obsolete symbol declared
    /// in a test project is fine — we just do not search test-project syntax trees for usages, so
    /// it is omitted unless production code calls it.
    /// <para>
    /// <see cref="SymbolResolver.AttributeIndex"/> is keyed by simple attribute name, and entries
    /// are duplicated under both <c>"Obsolete"</c> and <c>"ObsoleteAttribute"</c> — dedupe by
    /// symbol identity.
    /// </para>
    /// </remarks>
    internal static Dictionary<ISymbol, List<ObsoleteUsageSite>> Collect(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string? project)
    {
        var obsoleteSymbols = CollectObsoleteSymbols(resolver);
        if (obsoleteSymbols.Count == 0) return [];

        var targetSet = new HashSet<ISymbol>(obsoleteSymbols.Keys, SymbolEqualityComparer.Default);
        var usagesByTarget = new Dictionary<ISymbol, List<ObsoleteUsageSite>>(SymbolEqualityComparer.Default);
        foreach (var symbol in obsoleteSymbols.Keys)
            usagesByTarget[symbol] = [];

        var seen = new HashSet<(string, TextSpan)>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (IsSkippedProject(loaded, resolver, projectId, project)) continue;

            var projectName = resolver.GetProjectName(projectId);
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (tree.GetRoot() is not { } root) continue;
                CollectFromTree(compilation.GetSemanticModel(tree), root, projectName, resolver, targetSet, usagesByTarget, seen);
            }
        }

        // Symbols that no production tree references carry no report: dropping them here keeps the
        // caller's grouping loop free of a zero-usage filter.
        return usagesByTarget
            .Where(kvp => kvp.Value.Count > 0)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, SymbolEqualityComparer.Default);
    }

    /// <summary>
    /// The <c>[Obsolete]</c> symbols of the solution with their parsed message/error flag.
    /// </summary>
    internal static Dictionary<ISymbol, ObsoleteAttributeData> CollectObsoleteSymbols(SymbolResolver resolver)
    {
        var obsoleteSymbols = new Dictionary<ISymbol, ObsoleteAttributeData>(SymbolEqualityComparer.Default);
        foreach (var key in new[] { "Obsolete", "ObsoleteAttribute" })
        {
            if (!resolver.AttributeIndex.TryGetValue(key, out var entries)) continue;

            foreach (var (symbol, attr) in entries)
            {
                if (attr.AttributeClass?.ToDisplayString() != "System.ObsoleteAttribute") continue;
                if (obsoleteSymbols.ContainsKey(symbol)) continue;

                obsoleteSymbols[symbol] = ObsoleteAttributeData.FromAttribute(attr);
            }
        }

        return obsoleteSymbols;
    }

    private static bool IsSkippedProject(
        LoadedSolution loaded,
        SymbolResolver resolver,
        ProjectId projectId,
        string? project)
    {
        if (TestProjectDetector.GetTestProjectIds(loaded.Solution).Contains(projectId)) return true;

        if (project is null) return false;
        var projectName = resolver.GetProjectName(projectId);
        return !string.Equals(projectName, project, StringComparison.OrdinalIgnoreCase);
    }

    private static void CollectFromTree(
        SemanticModel semanticModel,
        SyntaxNode root,
        string projectName,
        SymbolResolver resolver,
        HashSet<ISymbol> targetSet,
        Dictionary<ISymbol, List<ObsoleteUsageSite>> usagesByTarget,
        HashSet<(string, TextSpan)> seen)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (!IsCandidateNode(node)) continue;
            if (node.FirstAncestorOrSelf<AttributeSyntax>() is not null) continue;
            if (IsWrappedByOuterRelevantNode(node)) continue;

            var symbol = semanticModel.GetSymbolInfo(node).Symbol;
            if (symbol is null) continue;

            if (MatchObsoleteSymbol(symbol, targetSet) is not { } matched) continue;

            var lineSpan = node.GetLocation().GetLineSpan();
            var file = lineSpan.Path;

            if (!seen.Add((file, node.Span))) continue;

            usagesByTarget[matched].Add(new ObsoleteUsageSite(
                CallerName: GetCallerName(node),
                FilePath: file,
                Line: lineSpan.StartLinePosition.Line + 1,
                Snippet: node.ToString(),
                Project: projectName,
                IsGenerated: resolver.IsGenerated(file)));
        }
    }

    private static bool IsCandidateNode(SyntaxNode node) =>
        node is InvocationExpressionSyntax
            or ObjectCreationExpressionSyntax
            or MemberAccessExpressionSyntax
            or IdentifierNameSyntax;

    // Returns true when `node` is a child of another node that the caller will already process
    // and that resolves to the same target symbol. Keeps usage counts at one-per-call-site.
    private static bool IsWrappedByOuterRelevantNode(SyntaxNode node)
    {
        var parent = node.Parent;
        if (parent is null) return false;

        if (IsDirectlyWrappedByOuterNode(parent, node))
            return true;

        // A qualified name contributes its .Right to whatever contains the whole name, so a
        // chain like TestLib.ObsoleteSamples.ObsoleteType is a duplicate only when its own
        // parent is already relevant. Recursing collapses deep chains to one site.
        return parent is QualifiedNameSyntax qualified
            && qualified.Right == node
            && IsWrappedByOuterRelevantNode(qualified);
    }

    /// <summary>
    /// Parent shapes whose own visit already accounts for this child: the enclosing node resolves
    /// to the same symbol and is processed by the caller instead.
    /// </summary>
    private static bool IsDirectlyWrappedByOuterNode(SyntaxNode parent, SyntaxNode node) => parent switch
    {
        // IdentifierName('ObsoleteWarning') is the .Name of MemberAccess in _api.ObsoleteWarning,
        // which will be visited; skip the IdentifierName.
        MemberAccessExpressionSyntax member => member.Name == node,

        // For obj?.ObsoleteMethod(), ?.ObsoleteMethod is a MemberBindingExpression whose .Name
        // is the IdentifierName.
        MemberBindingExpressionSyntax binding => binding.Name == node,

        // MemberAccess('obj.M') is the .Expression of Invocation. Invocation wins.
        InvocationExpressionSyntax invocation => invocation.Expression == node,

        // IdentifierName('ObsoleteType') is the .Type of new ObsoleteType(). ObjectCreation wins.
        ObjectCreationExpressionSyntax creation => creation.Type == node,

        // In new TestLib.ObsoleteSamples.ObsoleteType() the type IdentifierName is the .Right of
        // a QualifiedName that is itself the .Type of an ObjectCreation. ObjectCreation wins.
        QualifiedNameSyntax qualified => qualified.Right == node
            && qualified.Parent is ObjectCreationExpressionSyntax,

        _ => false,
    };

    private static ISymbol? MatchObsoleteSymbol(ISymbol referenced, HashSet<ISymbol> targetSet)
    {
        if (targetSet.Contains(referenced)) return referenced;
        if (targetSet.Contains(referenced.OriginalDefinition)) return referenced.OriginalDefinition;

        // Constructor calls: also match the containing type (when the type itself has [Obsolete]).
        if (referenced is IMethodSymbol m && m.MethodKind == MethodKind.Constructor)
        {
            if (m.ContainingType is not null && targetSet.Contains(m.ContainingType))
                return m.ContainingType;
            if (m.ContainingType?.OriginalDefinition is { } ctorTypeOrig && targetSet.Contains(ctorTypeOrig))
                return ctorTypeOrig;
        }

        return null;
    }

    private static string GetCallerName(SyntaxNode node)
    {
        var method = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var type = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();

        if (type != null && method != null)
            return $"{type.Identifier.Text}.{method.Identifier.Text}";
        if (type != null)
            return type.Identifier.Text;
        return "<unknown>";
    }
}
