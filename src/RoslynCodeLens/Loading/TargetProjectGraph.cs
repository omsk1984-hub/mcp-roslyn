using static RoslynCodeLens.ProjectClassifier;

namespace RoslynCodeLens.Loading;

/// <summary>
/// The on-disk ProjectReference graph restricted to a set of target projects, plus the
/// load order derived from it.
/// </summary>
/// <remarks>
/// Used by per-project loading: the graph both orders roots first (so a few large
/// transitive opens populate the capture cache and leaves are skipped before they are
/// ever scheduled) and re-wires <c>ProjectReference</c>s after re-stitch, because the
/// original reference ids were local to each worker workspace and mean nothing afterwards.
/// Split out of <c>SolutionLoader</c> (stage 5.3): it is pure XML/ graph work with no
/// dependency on Roslyn workspaces, timeouts, or skipping policy.
/// </remarks>
internal static class TargetProjectGraph
{
    /// <summary>
    /// Builds the reference graph limited to <paramref name="targets"/>: an edge exists only
    /// when both endpoints are in the target set, so references pointing outside the loaded
    /// closure never appear.
    /// </summary>
    public static TargetGraph Build(IReadOnlyList<ClassifiedProject> targets)
    {
        var targetPaths = new HashSet<string>(targets.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        var references = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in targets)
        {
            var refs = new List<string>();
            foreach (var refPath in ProjectGraphReader.ReadProjectReferences(t.Path))
            {
                if (targetPaths.Contains(refPath))
                    refs.Add(refPath);
            }
            references[t.Path] = refs;
        }

        return new TargetGraph(targetPaths, references);
    }

    /// <summary>
    /// Orders target projects by descending transitive in-set dependency count, so dependency
    /// roots open first. Their transitive loads populate the capture cache, letting leaf
    /// projects be skipped before they are ever scheduled. Ordering affects only efficiency,
    /// never correctness (the loader keeps the first capture per path).
    /// </summary>
    public static List<ClassifiedProject> OrderRootsFirst(
        IReadOnlyList<ClassifiedProject> targets,
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        int ClosureSize(string path)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!graph.TryGetValue(cur, out var refs))
                    continue;
                foreach (var r in refs)
                {
                    if (seen.Add(r))
                        stack.Push(r);
                }
            }
            return seen.Count;
        }

        return targets
            .OrderByDescending(t => ClosureSize(t.Path))
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>Target-set membership plus the restricted ProjectReference graph.</summary>
/// <param name="TargetPaths">Every target project path, ordinal-ignore-case.</param>
/// <param name="References">Path → in-set referenced paths; every target path has an entry.</param>
internal readonly record struct TargetGraph(
    HashSet<string> TargetPaths,
    Dictionary<string, IReadOnlyList<string>> References);
