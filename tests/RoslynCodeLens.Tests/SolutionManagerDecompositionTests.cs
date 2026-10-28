using Microsoft.CodeAnalysis;
using RoslynCodeLens.SolutionLifecycle;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Stage 5.2: <see cref="SolutionManager"/> (899 LOC / 61 members) is decomposed into a thin
/// façade over four collaborators. This file pins the collaborators' own contracts, so the
/// decomposition is proven by behaviour and not only by a line count.
///
/// <para>
/// The <see cref="SolutionManager"/> public surface is NOT re-tested here — it is pinned by
/// <c>SolutionManagerTests</c>, <c>IncrementalRebuildTests</c>, <c>LoaderConcurrencyHardeningTests</c>,
/// <c>MultiSolutionManagerTests</c> and <c>LegacySolutionHandlingTests</c>, all of which must stay
/// green WITHOUT edits. Those files are the contract that the move changed no behaviour.
/// </para>
/// </summary>
public class SolutionManagerDecompositionTests
{
    // ---------------------------------------------------------------------------------------
    // RecentChangesJournal — extracted first because it is the most isolated responsibility:
    // an accumulating, case-insensitive set of .cs paths that backs the `changedOnly` filter of
    // get_diagnostics. It owns its own gate; it touches no other manager state.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void RecentChangesJournal_RecordsPaths()
    {
        var journal = new RecentChangesJournal();

        journal.Record(@"C:/code/A.cs");
        journal.Record(@"C:/code/B.cs");

        Assert.Equal(
            [@"C:/code/A.cs", @"C:/code/B.cs"],
            journal.Snapshot().OrderBy(p => p, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// The journal is case-insensitive by construction: Windows paths differing only in casing
    /// denote the same file, and a duplicate entry would show the same document twice in the
    /// `changedOnly` output. Pinned on the extracted type because the set's comparer is part of
    /// the contract that must survive the move.
    /// </summary>
    [Fact]
    public void RecentChangesJournal_IsCaseInsensitive_AndDeduplicates()
    {
        var journal = new RecentChangesJournal();

        journal.Record(@"C:/code/A.cs");
        journal.Record(@"c:/CODE/a.CS");

        Assert.Single(journal.Snapshot());
    }

    [Fact]
    public void RecentChangesJournal_ResetClearsEverything()
    {
        var journal = new RecentChangesJournal();
        journal.Record(@"C:/code/A.cs");

        journal.Reset();

        Assert.Empty(journal.Snapshot());
    }

    /// <summary>
    /// <see cref="RecentChangesJournal.Snapshot"/> must hand back a COPY. Handing out the live
    /// set would let a caller mutate the journal through the returned list, and would make the
    /// snapshot racy for readers while a rebuild is recording into it.
    /// </summary>
    [Fact]
    public void RecentChangesJournal_SnapshotIsACopy()
    {
        var journal = new RecentChangesJournal();
        journal.Record(@"C:/code/A.cs");

        var first = journal.Snapshot();
        journal.Record(@"C:/code/B.cs");

        Assert.Single(first);
        Assert.Equal(2, journal.Snapshot().Count);
    }

    /// <summary>
    /// Concurrent recording from many threads must not lose or corrupt entries — the journal is
    /// written from the rebuild loop and from CommitDocumentTextsAsync, which can run on
    /// different threads. This is the thread-safety half of the field's former `lock (_lock)`.
    /// </summary>
    [Fact]
    public async Task RecentChangesJournal_ConcurrentRecords_AreNotLost()
    {
        var journal = new RecentChangesJournal();
        const int writers = 8;
        const int perWriter = 100;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
                journal.Record($@"C:/code/W{w}/F{i}.cs");
        })));

        Assert.Equal(writers * perWriter, journal.Snapshot().Count);
    }

    // ---------------------------------------------------------------------------------------
    // SolutionSnapshotStore — the core of invariant #282.
    //
    // The store holds the swappable triple (loaded solution + both resolvers) behind one lock.
    // #282 was a silent-empty bug: a caller could pair a Solution with a resolver built from a
    // DIFFERENT snapshot, so SymbolFinder returned nothing. The guarantee is that a reader never
    // observes a solution and a resolver from different swaps. These tests pin that directly.
    // ---------------------------------------------------------------------------------------

    private static (LoadedSolution Loaded, SymbolResolver Resolver, MetadataSymbolResolver Metadata)
        MakeSnapshot()
    {
        var loaded = SolutionSnapshotStore.CreateLoadedSolution(
            new AdhocWorkspace().CurrentSolution);
        var resolver = new SymbolResolver(loaded);
        return (loaded, resolver, new MetadataSymbolResolver(loaded, resolver));
    }

    [Fact]
    public void SolutionSnapshotStore_ReturnsTheInitialSnapshot()
    {
        var (loaded, resolver, metadata) = MakeSnapshot();
        var store = new SolutionSnapshotStore(loaded, resolver, metadata);

        var snapshot = store.Snapshot();

        Assert.Same(loaded, snapshot.Loaded);
        Assert.Same(resolver, snapshot.Resolver);
        Assert.Same(metadata, snapshot.Metadata);
    }

    /// <summary>
    /// After a swap the store must hand out the NEW triple — all three parts replaced together,
    /// never a mix of old and new. This is the #282 regression guard.
    /// </summary>
    [Fact]
    public void SolutionSnapshotStore_SwapReplacesAllThreeParts()
    {
        var (firstLoaded, firstResolver, firstMetadata) = MakeSnapshot();
        var store = new SolutionSnapshotStore(firstLoaded, firstResolver, firstMetadata);

        var (secondLoaded, secondResolver, secondMetadata) = MakeSnapshot();
        store.Swap(secondLoaded, secondResolver, secondMetadata);

        var snapshot = store.Snapshot();
        Assert.Same(secondLoaded, snapshot.Loaded);
        Assert.Same(secondResolver, snapshot.Resolver);
        Assert.Same(secondMetadata, snapshot.Metadata);
    }

    /// <summary>
    /// The actual #282 invariant under concurrency: a reader running while a writer swaps must
    /// only ever see a COMPLETE old triple or a COMPLETE new triple — never a solution from one
    /// swap paired with a resolver from the other. Each snapshot is self-identifying through the
    /// resolver's own <c>Loaded</c> reference, so a mismatch is detectable without any hooks.
    /// </summary>
    [Fact]
    public async Task SolutionSnapshotStore_ConcurrentSnapshotDuringSwap_NeverTears()
    {
        var (loadedA, resolverA, metadataA) = MakeSnapshot();
        var (loadedB, resolverB, metadataB) = MakeSnapshot();

        var store = new SolutionSnapshotStore(loadedA, resolverA, metadataA);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var tearing = new List<string>();
        var writer = Task.Run(() =>
        {
            var useB = true;
            while (!stop.IsCancellationRequested)
            {
                if (useB)
                    store.Swap(loadedB, resolverB, metadataB);
                else
                    store.Swap(loadedA, resolverA, metadataA);
                useB = !useB;
            }
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var snapshot = store.Snapshot();
                // A torn read would mix parts of A with parts of B. All three must agree on
                // which snapshot they came from.
                var loadedIsA = ReferenceEquals(snapshot.Loaded, loadedA);
                var resolverIsA = ReferenceEquals(snapshot.Resolver, resolverA);
                var metadataIsA = ReferenceEquals(snapshot.Metadata, metadataA);
                if (loadedIsA != resolverIsA || resolverIsA != metadataIsA)
                {
                    lock (tearing)
                        tearing.Add("torn snapshot observed");
                }
            }
        })).ToArray();

        await Task.WhenAll(readers.Append(writer));
        Assert.True(tearing.Count == 0, $"Observed {tearing.Count} torn snapshot(s) — #282 regressed.");
    }

    /// <summary>
    /// <c>CreateLoadedSolution</c> is the one place that builds an EMPTY (pre-warmup) snapshot with
    /// no compilations. It must preserve the solution/skips/diagnostics it is handed, because
    /// <c>SolutionManager.CreateAsync</c> relies on it to publish a queryable-but-unwarmed state.
    /// </summary>
    [Fact]
    public void SolutionSnapshotStore_CreateLoadedSolution_CarriesSkipsAndDiagnostics()
    {
        var solution = new AdhocWorkspace().CurrentSolution;
        var skipped = new[] { new SkippedProject("C:/code/A.csproj", "A", "Legacy", "non-SDK") };
        var diagnostics = new[] { "dropped reference to X" };

        var loaded = SolutionSnapshotStore.CreateLoadedSolution(solution, skipped, diagnostics);

        Assert.Same(solution, loaded.Solution);
        Assert.Empty(loaded.Compilations);
        Assert.Same(skipped, loaded.SkippedProjects);
        Assert.Same(diagnostics, loaded.LoadDiagnostics);
    }
}
