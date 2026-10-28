using RoslynCodeLens;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

// Пункт 5.4 этапа 5 (план `2026-10-28-solid-architecture-audit.md`): после декомпозиции
// `FindAsyncViolationsLogic.Execute` детекторы каждого из шести шаблонов должны быть
// самостоятельными членами, а не ветками одного метода-бога.
//
// Контракт: набор находок не меняется. Поэтому каждый тест сверяет результат детектора
// с подмножеством находок агрегирующего `Execute` — не с жёстко зашитыми числами.
[Collection("TestSolution")]
public class AsyncViolationDetectorTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public AsyncViolationDetectorTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    private static readonly AsyncViolationPattern[] Patterns =
    [
        AsyncViolationPattern.SyncOverAsyncResult,
        AsyncViolationPattern.SyncOverAsyncWait,
        AsyncViolationPattern.SyncOverAsyncGetAwaiterGetResult,
        AsyncViolationPattern.AsyncVoid,
        AsyncViolationPattern.MissingAwait,
        AsyncViolationPattern.FireAndForget,
    ];

    public static TheoryData<AsyncViolationPattern> AllPatterns => new(Patterns);

    [Theory]
    [MemberData(nameof(AllPatterns))]
    public void Detector_FindsExactlyTheViolationsOfItsOwnPattern(AsyncViolationPattern pattern)
    {
        var expected = FindAsyncViolationsLogic.Execute(_loaded, _resolver).Violations
            .Where(v => v.Pattern == pattern)
            .ToList();

        var actual = AsyncViolationDetector.DetectOnly(_loaded, _resolver, pattern);

        Assert.NotEmpty(expected);
        Assert.Equal(expected.Count, actual.Count);
        Assert.All(actual, v => Assert.Equal(pattern, v.Pattern));

        var expectedKeys = expected
            .Select(v => (v.FilePath, v.Line, v.ContainingMethod, v.Project))
            .OrderBy(k => k.FilePath, StringComparer.Ordinal)
            .ThenBy(k => k.Line)
            .ThenBy(k => k.ContainingMethod, StringComparer.Ordinal)
            .ToList();
        var actualKeys = actual
            .Select(v => (v.FilePath, v.Line, v.ContainingMethod, v.Project))
            .OrderBy(k => k.FilePath, StringComparer.Ordinal)
            .ThenBy(k => k.Line)
            .ThenBy(k => k.ContainingMethod, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expectedKeys, actualKeys);
    }

    [Fact]
    public void Detector_PerPatternUnionEqualsWholeScan()
    {
        // Полнота: объединение шести детекторов обязано дать ровно набор `Execute`.
        // Пропущенный шаблон или потерянная ветка ловится здесь, а не на глазок.
        var all = FindAsyncViolationsLogic.Execute(_loaded, _resolver).Violations.ToList();

        var union = new List<AsyncViolation>();
        foreach (var pattern in Patterns)
            union.AddRange(AsyncViolationDetector.DetectOnly(_loaded, _resolver, pattern));

        Assert.Equal(
            all.Select(v => (v.Pattern, v.FilePath, v.Line, v.ContainingMethod, v.Snippet)).OrderBy(k => k.Pattern).ThenBy(k => k.FilePath, StringComparer.Ordinal).ThenBy(k => k.Line).ToList(),
            union.Select(v => (v.Pattern, v.FilePath, v.Line, v.ContainingMethod, v.Snippet)).OrderBy(k => k.Pattern).ThenBy(k => k.FilePath, StringComparer.Ordinal).ThenBy(k => k.Line).ToList());
    }
}
