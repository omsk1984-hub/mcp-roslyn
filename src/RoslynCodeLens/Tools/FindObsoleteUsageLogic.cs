using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class FindObsoleteUsageLogic
{
    public static FindObsoleteUsageResult Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string? project,
        bool errorOnly)
    {
        // Walking the solution is ObsoleteUsageCollector's job; this method only filters,
        // scores and sorts. It does not know what a syntax tree is.
        var obsoleteSymbols = ObsoleteUsageCollector.CollectObsoleteSymbols(resolver);
        if (obsoleteSymbols.Count == 0)
            return new FindObsoleteUsageResult([]);

        var usagesByTarget = ObsoleteUsageCollector.Collect(loaded, resolver, project);
        var groups = BuildGroups(obsoleteSymbols, usagesByTarget, errorOnly);

        SortGroups(groups);
        return new FindObsoleteUsageResult(groups);
    }

    /// <summary>
    /// One group per obsolete symbol that has at least one surviving usage site. Symbols whose
    /// usages were all dropped by the collector's filters contribute nothing.
    /// </summary>
    private static List<ObsoleteSymbolGroup> BuildGroups(
        Dictionary<ISymbol, ObsoleteAttributeData> obsoleteSymbols,
        Dictionary<ISymbol, List<ObsoleteUsageSite>> usagesByTarget,
        bool errorOnly)
    {
        var groups = new List<ObsoleteSymbolGroup>();

        foreach (var (symbol, data) in obsoleteSymbols)
        {
            if (!usagesByTarget.TryGetValue(symbol, out var usages)) continue;
            if (errorOnly && !data.IsError) continue;

            usages.Sort(CompareUsages);
            groups.Add(new ObsoleteSymbolGroup(
                SymbolName: symbol.ToDisplayString(),
                DeprecationMessage: data.Message,
                IsError: data.IsError,
                UsageCount: usages.Count,
                Usages: usages));
        }

        return groups;
    }

    private static int CompareUsages(ObsoleteUsageSite a, ObsoleteUsageSite b)
    {
        var fileCmp = string.CompareOrdinal(a.FilePath, b.FilePath);
        return fileCmp != 0 ? fileCmp : a.Line.CompareTo(b.Line);
    }

    // Errors first, then highest usage count, then name — a stable, deterministic report order.
    private static void SortGroups(List<ObsoleteSymbolGroup> groups) =>
        groups.Sort((a, b) =>
        {
            var bySeverity = b.IsError.CompareTo(a.IsError);
            if (bySeverity != 0) return bySeverity;
            var byCount = b.UsageCount.CompareTo(a.UsageCount);
            if (byCount != 0) return byCount;
            return string.CompareOrdinal(a.SymbolName, b.SymbolName);
        });
}

/// <summary>
/// The message and error flag of a single <c>[Obsolete]</c> attribute application.
/// </summary>
internal readonly record struct ObsoleteAttributeData(string Message, bool IsError)
{
    internal static ObsoleteAttributeData FromAttribute(AttributeData attr)
    {
        var message = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string s
            ? s
            : string.Empty;
        var isError = attr.ConstructorArguments.Length > 1 && attr.ConstructorArguments[1].Value is bool b && b;
        return new ObsoleteAttributeData(message, isError);
    }
}
