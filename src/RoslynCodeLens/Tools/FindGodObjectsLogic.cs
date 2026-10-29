using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class FindGodObjectsLogic
{
    private static bool IsBclNamespace(string ns)
        => string.Equals(ns, "System")
           || string.Equals(ns, "Microsoft")
           || ns.StartsWith("System.", StringComparison.Ordinal)
           || ns.StartsWith("Microsoft.", StringComparison.Ordinal);

    public static GodObjectsResult Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string? project,
        int minLines,
        int minMembers,
        int minFields,
        int minIncomingNamespaces,
        int minOutgoingNamespaces)
    {
        var thresholds = new Thresholds(
            minLines, minMembers, minFields, minIncomingNamespaces, minOutgoingNamespaces);

        // Pass 1 — collect size-suspects (cheap).
        var suspects = CollectSizeSuspects(loaded, resolver, project, thresholds);
        if (suspects.Count == 0)
            return new GodObjectsResult([]);

        // Pass 2 — compute incoming-namespace coupling for each suspect with one solution-wide walk.
        var incomingByType = ComputeIncomingNamespaces(loaded, suspects);

        // Pass 3 — outgoing coupling per suspect, then rank.
        var results = CollectCoupledObjects(loaded, resolver, suspects, incomingByType, thresholds);
        results.Sort(CompareGodObjects);

        return new GodObjectsResult(results);
    }

    /// <summary>The user-facing cut-offs, threaded as one value through all three passes.</summary>
    private readonly record struct Thresholds(
        int MinLines,
        int MinMembers,
        int MinFields,
        int MinIncomingNamespaces,
        int MinOutgoingNamespaces);

    /// <summary>Pass 1: types that exceed all three size axes, before any coupling is computed.</summary>
    private static List<SizeSuspect> CollectSizeSuspects(
        LoadedSolution loaded, SymbolResolver resolver, string? project, Thresholds thresholds)
    {
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);

        // Cache project name → project id once. Avoids O(P) project enumeration per candidate
        // type in IsCandidate.
        var projectIdsByName = new Dictionary<string, ProjectId>(StringComparer.Ordinal);
        foreach (var p in loaded.Solution.Projects)
            projectIdsByName[p.Name] = p.Id;

        var suspects = new List<SizeSuspect>();

        foreach (var type in resolver.AllTypes)
        {
            if (!IsCandidate(type, testProjectIds, projectIdsByName, resolver, project))
                continue;

            var size = MeasureSize(type);
            var sizeAxes = CountExceededAxes(
                (size.LineCount, thresholds.MinLines),
                (size.MemberCount, thresholds.MinMembers),
                (size.FieldCount, thresholds.MinFields));

            // All three size axes must be exceeded for the type to be a god-object suspect.
            if (sizeAxes < 3) continue;

            suspects.Add(new SizeSuspect(type, size.LineCount, size.MemberCount, size.FieldCount, sizeAxes));
        }

        return suspects;
    }

    private static (int LineCount, int MemberCount, int FieldCount) MeasureSize(INamedTypeSymbol type) => (
        GetLineCount(type),
        type.GetMembers().Count(m => !m.IsImplicitlyDeclared),
        type.GetMembers().OfType<IFieldSymbol>().Count(f => !f.IsImplicitlyDeclared));

    /// <summary>Pass 3: keep suspects whose coupling crosses the threshold, and describe each one.</summary>
    private static List<GodObjectInfo> CollectCoupledObjects(
        LoadedSolution loaded,
        SymbolResolver resolver,
        List<SizeSuspect> suspects,
        Dictionary<INamedTypeSymbol, HashSet<string>> incomingByType,
        Thresholds thresholds)
    {
        var results = new List<GodObjectInfo>();

        foreach (var s in suspects)
        {
            var ownNamespace = s.Type.ContainingNamespace?.ToDisplayString() ?? string.Empty;

            // ComputeIncomingNamespaces pre-populates an entry for every suspect, so this lookup
            // always succeeds — the indexer is safe.
            var incoming = incomingByType[s.Type];
            var outgoing = ComputeOutgoingNamespaces(loaded, s.Type, ownNamespace);

            var couplingAxes = CountExceededAxes(
                (incoming.Count, thresholds.MinIncomingNamespaces),
                (outgoing.Count, thresholds.MinOutgoingNamespaces));

            if (couplingAxes == 0) continue;

            results.Add(DescribeGodObject(resolver, s, incoming, outgoing, couplingAxes));
        }

        return results;
    }

    private static GodObjectInfo DescribeGodObject(
        SymbolResolver resolver,
        SizeSuspect suspect,
        HashSet<string> incoming,
        HashSet<string> outgoing,
        int couplingAxes)
    {
        var (file, line) = resolver.GetFileAndLine(suspect.Type);

        return new GodObjectInfo(
            TypeName: suspect.Type.ToDisplayString(),
            LineCount: suspect.LineCount,
            MemberCount: suspect.MemberCount,
            FieldCount: suspect.FieldCount,
            IncomingNamespaces: incoming.Count,
            OutgoingNamespaces: outgoing.Count,
            SampleIncoming: Sample(incoming),
            SampleOutgoing: Sample(outgoing),
            FilePath: file,
            Line: line,
            Project: resolver.GetProjectName(suspect.Type),
            SizeAxesExceeded: suspect.SizeAxes,
            CouplingAxesExceeded: couplingAxes);
    }

    /// <summary>The five names shown in the report, alphabetically — deterministic between runs.</summary>
    private static List<string> Sample(HashSet<string> namespaces) =>
        namespaces.OrderBy(x => x, StringComparer.Ordinal).Take(5).ToList();

    private static int CountExceededAxes(params (int Actual, int Threshold)[] axes) =>
        axes.Count(axis => axis.Actual >= axis.Threshold);

    /// <summary>Worst first by total axes exceeded, then by size.</summary>
    private static int CompareGodObjects(GodObjectInfo a, GodObjectInfo b)
    {
        var byTotal = (b.SizeAxesExceeded + b.CouplingAxesExceeded)
            .CompareTo(a.SizeAxesExceeded + a.CouplingAxesExceeded);

        return byTotal != 0 ? byTotal : b.LineCount.CompareTo(a.LineCount);
    }

    private record SizeSuspect(INamedTypeSymbol Type, int LineCount, int MemberCount, int FieldCount, int SizeAxes);

    private static bool IsCandidate(
        INamedTypeSymbol type,
        ImmutableHashSet<ProjectId> testProjectIds,
        IReadOnlyDictionary<string, ProjectId> projectIdsByName,
        SymbolResolver resolver,
        string? project)
    {
        // Records report as Class/Struct in Roslyn, so this filter covers class/struct/record-class/record-struct.
        if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct) return false;
        if (type.ContainingType is not null) return false; // skip nested
        if (type.IsImplicitlyDeclared) return false;
        if (!type.Locations.Any(l => l.IsInSource)) return false;

        var location = type.Locations.First(l => l.IsInSource);
        if (location.SourceTree is not null && GeneratedCodeDetector.IsGenerated(location.SourceTree))
            return false;

        var projectName = resolver.GetProjectName(type);
        if (projectIdsByName.TryGetValue(projectName, out var projectId) && testProjectIds.Contains(projectId))
            return false;

        if (project is not null && !string.Equals(projectName, project, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static Dictionary<INamedTypeSymbol, HashSet<string>> ComputeIncomingNamespaces(
        LoadedSolution loaded, List<SizeSuspect> suspects)
    {
        var byType = new Dictionary<INamedTypeSymbol, HashSet<string>>(SymbolEqualityComparer.Default);
        foreach (var s in suspects)
            byType[s.Type] = new HashSet<string>(StringComparer.Ordinal);

        var suspectSet = new HashSet<INamedTypeSymbol>(suspects.Select(s => s.Type), SymbolEqualityComparer.Default);

        foreach (var (_, compilation) in loaded.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (GeneratedCodeDetector.IsGenerated(tree)) continue;

                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                foreach (var node in root.DescendantNodes())
                {
                    var symbol = semanticModel.GetSymbolInfo(node).Symbol;
                    if (symbol is null) continue;

                    INamedTypeSymbol? containingType = null;
                    if (symbol is INamedTypeSymbol nts)
                        containingType = nts.OriginalDefinition;
                    else if (symbol.ContainingType is not null)
                        containingType = symbol.ContainingType.OriginalDefinition;

                    if (containingType is null) continue;
                    if (!suspectSet.Contains(containingType)) continue;

                    var callerNamespace = GetEnclosingNamespace(node, semanticModel);
                    if (callerNamespace is null) continue;

                    var ownNamespace = containingType.ContainingNamespace?.ToDisplayString();
                    if (string.Equals(callerNamespace, ownNamespace, StringComparison.Ordinal)) continue;

                    byType[containingType].Add(callerNamespace);
                }
            }
        }

        return byType;
    }

    private static HashSet<string> ComputeOutgoingNamespaces(
        LoadedSolution loaded, INamedTypeSymbol type, string ownNamespace)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declRef in type.DeclaringSyntaxReferences)
        {
            var tree = declRef.SyntaxTree;
            Compilation? compilation = null;
            foreach (var (_, comp) in loaded.Compilations)
            {
                if (comp.SyntaxTrees.Contains(tree))
                {
                    compilation = comp;
                    break;
                }
            }
            if (compilation is null) continue;

            var semanticModel = compilation.GetSemanticModel(tree);
            var bodyNode = declRef.GetSyntax();

            foreach (var node in bodyNode.DescendantNodes())
            {
                var symbol = semanticModel.GetSymbolInfo(node).Symbol;
                if (symbol is null) continue;

                var ns = symbol.ContainingType?.ContainingNamespace?.ToDisplayString();
                if (string.IsNullOrEmpty(ns) && symbol is INamedTypeSymbol nts2)
                    ns = nts2.ContainingNamespace?.ToDisplayString();
                if (string.IsNullOrEmpty(ns)) continue;

                if (string.Equals(ns, ownNamespace, StringComparison.Ordinal)) continue;
                if (IsBclNamespace(ns!)) continue;

                result.Add(ns);
            }
        }

        return result;
    }

    private static string? GetEnclosingNamespace(SyntaxNode node, SemanticModel semanticModel)
    {
        // Prefer the innermost enclosing type declaration. For top-level statements (no
        // enclosing type in syntax), fall back to file-scoped/braced namespace nodes.
        var typeDecl = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl is not null)
        {
            var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl);
            return typeSymbol?.ContainingNamespace?.ToDisplayString();
        }

        var nsDecl = node.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (nsDecl is not null)
        {
            var nsSymbol = semanticModel.GetDeclaredSymbol(nsDecl);
            return nsSymbol?.ToDisplayString();
        }

        // Top-level statement in the global namespace — represent as empty string so it can
        // still be added to the incoming-namespace set (and won't equal any real namespace).
        return string.Empty;
    }

    private static int GetLineCount(INamedTypeSymbol type)
    {
        // Sum line counts across all partial declarations. A 400-line partial split into two
        // 200-line files is just as much code to maintain as a single 400-line file.
        var total = 0;
        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            var span = syntaxRef.Span;
            var tree = syntaxRef.SyntaxTree;
            var lineSpan = tree.GetLineSpan(span);
            total += lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;
        }
        return total;
    }
}
