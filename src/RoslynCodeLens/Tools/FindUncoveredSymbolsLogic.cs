using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class FindUncoveredSymbolsLogic
{
    private const int MaxDepth = 3;
    private const int RiskThreshold = 5;

    public static FindUncoveredSymbolsResult Execute(LoadedSolution loaded, SymbolResolver source)
    {
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);

        // 1. Walk callees DOWN from every test method to build the covered set.
        var coveredSet = BuildCoveredSet(loaded, testProjectIds);

        // 2. Enumerate candidates: public methods + properties from non-test projects.
        var candidates = EnumerateCandidates(loaded, source, testProjectIds);

        // 3. Diff and build output.
        var uncovered = new List<UncoveredSymbol>();
        var coveredCount = 0;
        foreach (var candidate in candidates)
        {
            if (IsCovered(candidate.Symbol, coveredSet))
                coveredCount++;
            else
                uncovered.Add(BuildUncoveredSymbol(candidate.Symbol, candidate.ProjectName));
        }

        // 4. Sort: complexity DESC, then symbol name ASC.
        uncovered.Sort((a, b) =>
        {
            var byComplexity = b.Complexity.CompareTo(a.Complexity);
            return byComplexity != 0
                ? byComplexity
                : string.CompareOrdinal(a.Symbol, b.Symbol);
        });

        // 5. Summary.
        var total = candidates.Count;
        var coveragePercent = total == 0
            ? 100
            : (int)Math.Floor((double)coveredCount / total * 100);
        var riskHotspotCount = uncovered.Count(s => s.Complexity >= RiskThreshold);
        var summary = new CoverageSummary(
            TotalSymbols: total,
            CoveredCount: coveredCount,
            UncoveredCount: uncovered.Count,
            CoveragePercent: coveragePercent,
            RiskHotspotCount: riskHotspotCount);

        return new FindUncoveredSymbolsResult(summary, uncovered);
    }

    private static HashSet<IMethodSymbol> BuildCoveredSet(
        LoadedSolution loaded,
        ImmutableHashSet<ProjectId> testProjectIds)
    {
        var covered = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<(IMethodSymbol Method, int Depth)>();

        // Seed: every test method, depth 0.
        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (!testProjectIds.Contains(projectId))
                continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();
                foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (semanticModel.GetDeclaredSymbol(methodDecl) is not IMethodSymbol method)
                        continue;
                    if (!TestMethodClassifier.IsTestMethod(method))
                        continue;
                    if (visited.Add(method))
                        queue.Enqueue((method, 0));
                }
            }
        }

        // BFS down through callees up to MaxDepth.
        while (queue.Count > 0)
        {
            var (frontier, depth) = queue.Dequeue();
            if (depth >= MaxDepth)
                continue;

            foreach (var callee in EnumerateCallees(frontier, loaded))
            {
                var original = callee.OriginalDefinition;
                if (!visited.Add(original))
                    continue;
                covered.Add(original);
                queue.Enqueue((original, depth + 1));
            }
        }

        return covered;
    }

    private static IEnumerable<IMethodSymbol> EnumerateCallees(IMethodSymbol method, LoadedSolution loaded)
    {
        foreach (var location in method.Locations)
        {
            if (!location.IsInSource)
                continue;
            var tree = location.SourceTree;
            if (tree is null)
                continue;

            // Find the compilation that owns this tree.
            Compilation? compilation = null;
            foreach (var (_, comp) in loaded.Compilations)
            {
                if (comp.SyntaxTrees.Contains(tree))
                {
                    compilation = comp;
                    break;
                }
            }
            if (compilation is null)
                continue;

            var semanticModel = compilation.GetSemanticModel(tree);
            var declNode = tree.GetRoot().FindNode(location.SourceSpan);

            // Method invocations.
            foreach (var invocation in declNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (semanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol called)
                    yield return called;
            }

            // Property accesses — return both accessors so either reading or writing the
            // property covers it.
            foreach (var memberAccess in declNode.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (semanticModel.GetSymbolInfo(memberAccess).Symbol is IPropertySymbol prop)
                {
                    if (prop.GetMethod is not null)
                        yield return prop.GetMethod;
                    if (prop.SetMethod is not null)
                        yield return prop.SetMethod;
                }
            }
        }
    }

    private sealed record CandidateInfo(ISymbol Symbol, string ProjectName);

    /// <summary>
    /// Enumerates the public, source-declared members of every non-test project — the
    /// universe the coverage diff is taken against. Structured as the pipeline it is:
    /// walk projects, keep eligible types, keep eligible members, classify each survivor.
    /// </summary>
    private static IReadOnlyList<CandidateInfo> EnumerateCandidates(
        LoadedSolution loaded,
        SymbolResolver source,
        ImmutableHashSet<ProjectId> testProjectIds)
    {
        var candidates = new List<CandidateInfo>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (testProjectIds.Contains(projectId))
                continue;

            CollectCandidatesFromCompilation(
                compilation, source.GetProjectName(projectId), candidates);
        }

        return candidates;
    }

    private static void CollectCandidatesFromCompilation(
        Compilation compilation, string projectName, List<CandidateInfo> candidates)
    {
        foreach (var type in EnumerateTypes(compilation.Assembly.GlobalNamespace))
        {
            if (!IsEligibleType(type))
                continue;

            foreach (var member in type.GetMembers())
            {
                if (ClassifyCandidateMember(member) is { } symbol)
                    candidates.Add(new CandidateInfo(symbol, projectName));
            }
        }
    }

    /// <summary>
    /// A type contributes candidates only when it is public and actually declared in
    /// source: metadata-only types (referenced assemblies) have no lines to report.
    /// </summary>
    private static bool IsEligibleType(INamedTypeSymbol type)
        => type.DeclaredAccessibility == Accessibility.Public
            && type.Locations.Any(l => l.IsInSource);

    /// <summary>
    /// Returns the symbol to report for this member, or <c>null</c> when the member is
    /// not a coverage candidate. Only ordinary, non-abstract, source-declared methods and
    /// non-indexer, non-abstract properties with at least one accessor qualify — implicit
    /// members (auto-property backing fields, accessors) are never reported on their own.
    /// </summary>
    private static ISymbol? ClassifyCandidateMember(ISymbol member)
    {
        if (member.DeclaredAccessibility != Accessibility.Public)
            return null;
        if (!member.Locations.Any(l => l.IsInSource))
            return null;
        if (member.IsImplicitlyDeclared)
            return null;

        return member switch
        {
            IMethodSymbol method => IsCandidateMethod(method) ? method : null,
            IPropertySymbol property => IsCandidateProperty(property) ? property : null,
            _ => null,
        };
    }

    /// <summary>
    /// Abstract methods are excluded because they have no body to cover — the override
    /// that a test would actually reach is reported separately. Non-ordinary methods
    /// (accessors, operators, constructors, local functions) are covered through their
    /// owning member.
    /// </summary>
    private static bool IsCandidateMethod(IMethodSymbol method)
        => method.MethodKind == MethodKind.Ordinary && !method.IsAbstract;

    private static bool IsCandidateProperty(IPropertySymbol property)
        => !property.IsIndexer
            && !property.IsAbstract
            && (property.GetMethod is not null || property.SetMethod is not null);

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in EnumerateNestedTypes(type))
                yield return nested;
        }
        foreach (var nested in ns.GetNamespaceMembers())
            foreach (var type in EnumerateTypes(nested))
                yield return type;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in EnumerateNestedTypes(nested))
                yield return deeper;
        }
    }

    private static bool IsCovered(ISymbol candidate, HashSet<IMethodSymbol> coveredSet)
    {
        if (candidate is IMethodSymbol method)
            return coveredSet.Contains(method.OriginalDefinition);
        if (candidate is IPropertySymbol property)
            return (property.GetMethod is not null && coveredSet.Contains(property.GetMethod.OriginalDefinition))
                || (property.SetMethod is not null && coveredSet.Contains(property.SetMethod.OriginalDefinition));
        return false;
    }

    private static UncoveredSymbol BuildUncoveredSymbol(ISymbol symbol, string projectName)
    {
        var location = symbol.Locations.First(l => l.IsInSource);
        var lineSpan = location.GetLineSpan();
        var kind = symbol is IMethodSymbol ? UncoveredSymbolKind.Method : UncoveredSymbolKind.Property;
        var symbolName = symbol.ContainingType is null
            ? symbol.Name
            : $"{symbol.ContainingType.Name}.{symbol.Name}";
        var complexity = ComputeComplexity(symbol);

        return new UncoveredSymbol(
            Symbol: symbolName,
            Kind: kind,
            FilePath: lineSpan.Path,
            Line: lineSpan.StartLinePosition.Line + 1,
            Project: projectName,
            Complexity: complexity);
    }

    private static int ComputeComplexity(ISymbol symbol)
    {
        var maxComplexity = 1;
        foreach (var location in symbol.Locations)
        {
            if (!location.IsInSource)
                continue;
            var tree = location.SourceTree;
            if (tree is null)
                continue;

            var node = tree.GetRoot().FindNode(location.SourceSpan);

            if (node is MethodDeclarationSyntax)
            {
                maxComplexity = Math.Max(maxComplexity, ComplexityCalculator.Calculate(node));
            }
            else if (node is PropertyDeclarationSyntax property)
            {
                if (property.AccessorList is not null)
                {
                    foreach (var accessor in property.AccessorList.Accessors)
                        maxComplexity = Math.Max(maxComplexity, ComplexityCalculator.Calculate(accessor));
                }
                else if (property.ExpressionBody is not null)
                {
                    maxComplexity = Math.Max(maxComplexity, ComplexityCalculator.Calculate(property.ExpressionBody));
                }
            }
        }
        return maxComplexity;
    }
}
