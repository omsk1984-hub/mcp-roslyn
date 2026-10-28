using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

// Пункт 5.4 этапа 5 (план `2026-10-28-solid-architecture-audit.md`): обход решения должен быть
// отделён от группировки и подсчёта. `Execute` после декомпозиции только вызывает коллектор,
// фильтрует по `errorOnly` и сортирует.
//
// Контракт: набор находок не меняется — коллектор обязан вернуть ровно те использования,
// которые попадают в группы `Execute`.
[Collection("TestSolution")]
public class ObsoleteUsageCollectorTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public ObsoleteUsageCollectorTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void Collect_GathersEveryUsageThatExecuteReports()
    {
        var expected = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false)
            .Groups
            .SelectMany(g => g.Usages.Select(u => (g.SymbolName, u.FilePath, u.Line, u.CallerName, u.Project)))
            .OrderBy(k => k.SymbolName, StringComparer.Ordinal)
            .ThenBy(k => k.FilePath, StringComparer.Ordinal)
            .ThenBy(k => k.Line)
            .ToList();

        var actual = ObsoleteUsageCollector.Collect(_loaded, _resolver, project: null)
            .SelectMany(kvp => kvp.Value.Select(u => (kvp.Key.ToDisplayString(), u.FilePath, u.Line, u.CallerName, u.Project)))
            .OrderBy(k => k.Item1, StringComparer.Ordinal)
            .ThenBy(k => k.FilePath, StringComparer.Ordinal)
            .ThenBy(k => k.Line)
            .ToList();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Collect_DropsSymbolsWithoutUsages()
    {
        var collected = ObsoleteUsageCollector.Collect(_loaded, _resolver, project: null);

        Assert.All(collected, kvp => Assert.NotEmpty(kvp.Value));
    }

    [Fact]
    public void Collect_RespectsProjectFilter()
    {
        // Фильтр должен сужать обход, а не обрезать результат постфактум: символы, у которых
        // после фильтрации не осталось использований, в словарь не попадают.
        var all = ObsoleteUsageCollector.Collect(_loaded, _resolver, project: null);
        var someProject = all.SelectMany(kvp => kvp.Value).Select(u => u.Project).First();

        var filtered = ObsoleteUsageCollector.Collect(_loaded, _resolver, project: someProject);

        Assert.NotEmpty(filtered);
        Assert.All(filtered, kvp => Assert.All(kvp.Value, u => Assert.Equal(someProject, u.Project)));
        Assert.True(filtered.Count <= all.Count);
    }
}
