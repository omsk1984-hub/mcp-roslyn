using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class FindAsyncViolationsLogic
{
    public static FindAsyncViolationsResult Execute(LoadedSolution loaded, SymbolResolver source)
    {
        // The six detectors live in AsyncViolationDetector, one method per pattern. This method
        // only aggregates: collect, sort, summarise. Adding or changing a pattern must not
        // require touching the scan, and vice versa.
        var violations = AsyncViolationDetector.DetectAll(loaded, source);
        SortBySeverityThenLocation(violations);

        return new FindAsyncViolationsResult(BuildSummary(violations), violations);
    }

    // Sort: severity ASC (Error=0 first), then file ASC, then line ASC.
    private static void SortBySeverityThenLocation(List<AsyncViolation> violations) =>
        violations.Sort((a, b) =>
        {
            var bySeverity = ((int)a.Severity).CompareTo((int)b.Severity);
            if (bySeverity != 0) return bySeverity;
            var byPath = string.CompareOrdinal(a.FilePath, b.FilePath);
            if (byPath != 0) return byPath;
            return a.Line.CompareTo(b.Line);
        });

    private static AsyncViolationSummary BuildSummary(List<AsyncViolation> violations) => new(
        TotalViolations: violations.Count,
        ByPattern: violations
            .GroupBy(v => v.Pattern.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        BySeverity: violations
            .GroupBy(v => v.Severity.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));
}
