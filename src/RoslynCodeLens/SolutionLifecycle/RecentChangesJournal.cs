namespace RoslynCodeLens.SolutionLifecycle;

/// <summary>
/// The journal of .cs document paths changed since the last explicit reset. Extracted from
/// <see cref="SolutionManager"/> (stage 5.2) as the most isolated of its responsibilities: it
/// owns a single set and no other manager state, so it needs neither the solution snapshot nor
/// the rebuild gate.
///
/// <para>
/// Accumulated by every incremental rebuild and by
/// <see cref="SolutionManager.CommitDocumentTextsAsync"/>. Backs the <c>changedOnly</c> filter of
/// <c>get_diagnostics</c>: unlike the tracker's live stale set (which is drained by the rebuild
/// that consumes it), this journal survives the rebuild, so a query issued right after an edit
/// still sees "what changed".
/// </para>
///
/// <para>
/// A full reload calls <see cref="Reset"/>: after a reload every project was recompiled from
/// disk, so "changed since last reset" is empty by definition.
/// </para>
///
/// <para>
/// Case-insensitive on purpose — Windows paths differing only in casing denote the same file, and
/// a duplicate would report the same document twice. Thread-safe: the rebuild loop and
/// <c>CommitDocumentTextsAsync</c> can record from different threads.
/// </para>
/// </summary>
public sealed class RecentChangesJournal
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>
    /// Adds <paramref name="path"/> to the journal. Idempotent: the set de-duplicates.
    /// </summary>
    public void Record(string path)
    {
        lock (_lock)
            _paths.Add(path);
    }

    /// <summary>
    /// The recorded paths as a COPY, so a caller can neither mutate the journal through the
    /// result nor observe a concurrent write half-way through.
    /// </summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_lock)
            return _paths.ToList();
    }

    /// <summary>Clears the journal, so the next <c>changedOnly</c> query starts from a clean slate.</summary>
    public void Reset()
    {
        lock (_lock)
            _paths.Clear();
    }
}
