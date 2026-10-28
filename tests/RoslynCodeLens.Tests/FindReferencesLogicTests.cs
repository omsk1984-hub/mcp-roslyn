using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Per-occurrence reporting: references are keyed by (file, line, column), so several
/// references on one line survive as distinct items with their own kinds.
/// </summary>
public class FindReferencesLogicTests
{
    private static Task<IReadOnlyList<SymbolReference>> FindAsync(string source, string symbol)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Sample.cs", source));
        var metadata = new MetadataSymbolResolver(loaded, resolver);
        return FindReferencesLogic.ExecuteAsync(loaded, resolver, metadata, symbol, CancellationToken.None);
    }

    [Fact]
    public async Task SameLineMultiRef_YieldsTwoItems()
    {
        const string source = "class C { int _x; void M() { _x = _x + 1; } }";

        var results = await FindAsync(source, "C._x");

        var onLineOne = results.Where(r => r.Line == 1).ToList();
        Assert.Equal(2, onLineOne.Count);
        Assert.Equal(2, onLineOne.Select(r => r.Column).Distinct().Count());
    }

    [Fact]
    public async Task Write_And_Read_Classified()
    {
        const string source = "class C { int _x; void M() { _x = _x + 1; } }";

        var results = (await FindAsync(source, "C._x")).OrderBy(r => r.Column).ToList();

        Assert.Equal(["write", "read"], results.Select(r => r.ReferenceKind));
        Assert.True(results[0].Column < results[1].Column);
    }

    [Fact]
    public async Task Invocation_Classified()
    {
        const string source = "class C { void M() { } void U() { M(); } }";

        var results = await FindAsync(source, "C.M");

        Assert.Contains(results, r => string.Equals(r.ReferenceKind, "invocation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypeReference_Kinds()
    {
        const string source = """
            class Foo { }
            class Bar : Foo { }
            class C
            {
                void U()
                {
                    var f = new Foo();
                    var t = typeof(Foo);
                }
            }
            """;

        var kinds = (await FindAsync(source, "Foo")).Select(r => r.ReferenceKind).ToList();

        Assert.Contains("base_type", kinds, StringComparer.Ordinal);
        Assert.Contains("object_creation", kinds, StringComparer.Ordinal);
        Assert.Contains("typeof", kinds, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Column_IsOneBased()
    {
        // "class C { int _x; void M() { _x = 1; } }"
        //                               ^ index 29 (0-based) -> column 30
        const string source = "class C { int _x; void M() { _x = 1; } }";

        var write = Assert.Single(
            await FindAsync(source, "C._x"), r => string.Equals(r.ReferenceKind, "write", StringComparison.Ordinal));

        Assert.Equal(source.IndexOf("_x = 1", StringComparison.Ordinal) + 1, write.Column);
    }

    /// <summary>
    /// Stage 4/B: the async scan must not occupy the calling thread while
    /// <see cref="SymbolFinder.FindReferencesAsync"/> (and the lazily built semantic model) are in
    /// flight. A sync-over-async implementation returns an already-completed task — the awaited
    /// symbol search ran to completion before the method handed anything back — so
    /// <c>IsCompleted</c> alone separates the two shapes without timing assumptions.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DoesNotBlockCallingThread()
    {
        const string source = "class C { int _x; void M() { _x = _x + 1; } }";
        var (loaded, resolver) = RenameTestWorkspace.Create(("Sample.cs", source));
        var metadata = new MetadataSymbolResolver(loaded, resolver);

        var pending = FindReferencesLogic.ExecuteAsync(
            loaded, resolver, metadata, "C._x", CancellationToken.None);

        // A blocking implementation would already be finished here, having parked the calling
        // thread on SymbolFinder.FindReferencesAsync(...).GetAwaiter().GetResult().
        Assert.False(pending.IsCompleted,
            "ExecuteAsync returned synchronously — the reference scan is still blocking the calling thread.");

        var results = await pending;
        Assert.Equal(2, results.Count(r => r.Line == 1));
    }

    /// <summary>
    /// Stage 4/B: cancellation has to surface as <see cref="OperationCanceledException"/> instead of
    /// a hung call — the pre-cancelled token must be observed before any SymbolFinder work starts.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CancelledToken_ThrowsInsteadOfHanging()
    {
        const string source = "class C { int _x; void M() { _x = _x + 1; } }";
        var (loaded, resolver) = RenameTestWorkspace.Create(("Sample.cs", source));
        var metadata = new MetadataSymbolResolver(loaded, resolver);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var call = Task.Run(() => FindReferencesLogic.ExecuteAsync(
            loaded, resolver, metadata, "C._x", cts.Token));

        // Bound the wait: a hang (rather than a cancellation) must fail this test, not stall the run.
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(call, finished);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }
}
