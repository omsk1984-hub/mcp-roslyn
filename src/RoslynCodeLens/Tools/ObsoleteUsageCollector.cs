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

        // Wrapped by a member access whose `.Name` is this node:
        //   `_api.ObsoleteWarning` — IdentifierName('ObsoleteWarning') is the .Name of MemberAccess.
        //   The MemberAccess will be visited; skip the IdentifierName.
        if (parent is MemberAccessExpressionSyntax member && member.Name == node)
            return true;

        // Wrapped by member-binding (`obj?.ObsoleteMethod()` — `?.ObsoleteMethod` is a
        // MemberBindingExpression whose .Name is the IdentifierName).
        if (parent is MemberBindingExpressionSyntax binding && binding.Name == node)
            return true;

        // Wrapped by an invocation whose `.Expression` is this node:
        //   `obj.M()` — MemberAccess('obj.M') is the .Expression of Invocation. Invocation wins.
        if (parent is InvocationExpressionSyntax invocation && invocation.Expression == node)
            return true;

        // Wrapped by an object-creation whose `.Type` is this node:
        //   `new ObsoleteType()` — IdentifierName('ObsoleteType') is the .Type. ObjectCreation wins.
        if (parent is ObjectCreationExpressionSyntax creation && creation.Type == node)
            return true;

        // Wrapped by a qualified-name whose `.Right` is this node, when the qualified name itself
        // is the .Type of an object creation:
        //   `new TestLib.ObsoleteSamples.ObsoleteType()` — ObsoleteType IdentifierName is the
        //   .Right of a QualifiedName which is the .Type of ObjectCreation. ObjectCreation wins.
        if (parent is QualifiedNameSyntax qualified
            && qualified.Right == node
            && qualified.Parent is ObjectCreationExpressionSyntax)
            return true;

        // Wrapped by a qualified-name whose `.Right` is this node, when the qualified name's
        // parent is itself wrapped by an outer relevant node (recursive case for deep chains).
        if (parent is QualifiedNameSyntax qualifiedRecurse && qualifiedRecurse.Right == node)
            return IsWrappedByOuterRelevantNode(qualifiedRecurse);

        return false;
    }

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
