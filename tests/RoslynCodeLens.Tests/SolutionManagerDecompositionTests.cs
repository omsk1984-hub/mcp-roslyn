using RoslynCodeLens.SolutionLifecycle;

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
}
