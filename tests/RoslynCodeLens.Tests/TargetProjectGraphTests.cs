using RoslynCodeLens.Loading;
using static RoslynCodeLens.ProjectClassifier;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Pins the ordering contract that used to live in <c>SolutionLoader.OrderRootsFirst</c>:
/// dependency roots come first so their transitive opens populate the capture cache and
/// leaf projects are skipped before they are ever scheduled. Ordering affects only
/// efficiency, never correctness — the loader keeps the first capture per path.
/// </summary>
public sealed class TargetProjectGraphTests
{
    private static ClassifiedProject P(string path, string name, ProjectKind kind = ProjectKind.SdkStyle)
        => new(path, name, kind, null);

    [Fact]
    public void OrderRootsFirst_PutsProjectWithLargestClosureFirst()
    {
        // a -> b -> c ; d standalone. 'a' reaches b and c, so it must open first.
        var targets = new List<ClassifiedProject> { P("d.csproj", "D"), P("c.csproj", "C"), P("a.csproj", "A"), P("b.csproj", "B") };
        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["d.csproj"] = [],
            ["c.csproj"] = [],
            ["b.csproj"] = ["c.csproj"],
            ["a.csproj"] = ["b.csproj"],
        };

        var ordered = TargetProjectGraph.OrderRootsFirst(targets, graph);

        Assert.Equal("a.csproj", ordered[0].Path);
        Assert.Equal("b.csproj", ordered[1].Path);
    }

    [Fact]
    public void OrderRootsFirst_BreaksTiesByNameOrdinal()
    {
        var targets = new List<ClassifiedProject> { P("z.csproj", "Zeta"), P("a.csproj", "Alpha") };
        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["z.csproj"] = [],
            ["a.csproj"] = [],
        };

        var ordered = TargetProjectGraph.OrderRootsFirst(targets, graph);

        Assert.Equal(["Alpha", "Zeta"], ordered.Select(p => p.Name), StringComparer.Ordinal);
    }

    [Fact]
    public void OrderRootsFirst_SurvivesCycles()
    {
        // A <-> B is a legal (if unusual) ProjectReference cycle; closure counting must terminate.
        var targets = new List<ClassifiedProject> { P("a.csproj", "A"), P("b.csproj", "B") };
        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["a.csproj"] = ["b.csproj"],
            ["b.csproj"] = ["a.csproj"],
        };

        var ordered = TargetProjectGraph.OrderRootsFirst(targets, graph);

        Assert.Equal(2, ordered.Count);
    }

    [Fact]
    public void OrderRootsFirst_CountsReferencesOutsideTheTargetSetInClosureSize()
    {
        // 'B' points at a project that is not a target. It never gets opened (the loader only
        // schedules targets), but it still enlarges B's closure, so B is ordered first. Pinned
        // as-is: Build() never records out-of-set edges, and OrderRootsFirst is a pure sort over
        // whatever graph it is handed, so dangling entries only affect ordering, never correctness.
        var targets = new List<ClassifiedProject> { P("a.csproj", "A"), P("b.csproj", "B") };
        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["a.csproj"] = [],
            ["b.csproj"] = ["external.csproj", "external2.csproj"],
        };

        var ordered = TargetProjectGraph.OrderRootsFirst(targets, graph);

        Assert.Equal(["B", "A"], ordered.Select(p => p.Name), StringComparer.Ordinal);
    }

    [Fact]
    public void Build_ExcludesReferencesOutsideTheTargetSet()
    {
        // The filtered-out edge is what keeps the loader from scheduling a project that was
        // never a target (e.g. removed by a load filter or classified as legacy).
        var dir = Path.Combine(Path.GetTempPath(), "rcl-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "A.csproj");
            var b = Path.Combine(dir, "B.csproj");
            var outside = Path.Combine(dir, "Outside.csproj");
            File.WriteAllText(a, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><ProjectReference Include="{b}" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(b, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><ProjectReference Include="{outside}" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(outside, """<Project Sdk="Microsoft.NET.Sdk" />""");

            var graph = TargetProjectGraph.Build([P(a, "A"), P(b, "B")]);

            Assert.Equal([b], graph.References[a]);
            Assert.Empty(graph.References[b]);
            Assert.Equal(2, graph.TargetPaths.Count);
            Assert.DoesNotContain(outside, graph.TargetPaths);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
