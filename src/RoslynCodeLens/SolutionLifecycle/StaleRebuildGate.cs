namespace RoslynCodeLens.SolutionLifecycle;

/// <summary>
/// Serializes every operation that produces a new loaded solution — the watcher-driven auto-rebuild
/// and the explicit rebuild (<c>rebuild_solution</c>, <c>CommitDocumentTextsAsync</c>). Extracted
/// from <see cref="SolutionManager"/> (stage 5.2) so the single-flight rule and the
/// drain/restore bookkeeping live in one place instead of being spread through the manager.
///
/// <para>
/// <b>Why one gate for everything.</b> Only one producer may run at a time, otherwise two of them
/// can read the same snapshot, one swaps and disposes the workspace underneath the other, and the
/// second then clobbers the swap with a solution forked off a disposed workspace. Three entry
/// styles follow from that single rule:
/// </para>
/// <list type="bullet">
/// <item><see cref="RunExclusiveAsync"/> — waits for the gate. Used by the explicit reload and by
/// <c>CommitDocumentTextsAsync</c>, which must not be skipped.</item>
/// <item><see cref="TryRunExclusiveAsync"/> — opportunistic, never waits. Used by the watcher
/// rebuild: if a rebuild is already running, the query serves the current consistent data instead
/// of queueing behind a multi-second rebuild.</item>
/// <item><see cref="TryEnterAsync"/> / <see cref="Exit"/> — the raw pair, for callers that need to
/// hold the gate across operations the coordinator cannot express.</item>
/// </list>
///
/// <para>
/// <b>Drain/restore.</b> <see cref="RunExclusiveAsync"/> and <see cref="TryRunExclusiveAsync"/> take
/// a <c>restoreOnIncomplete</c> callback that runs when the body does NOT complete — failed or
/// cancelled. That is what stops the tracker's drained stale snapshot from being lost: the rebuild
/// re-arms the drained projects so the next query retries, instead of silently serving stale data
/// forever.
/// </para>
///
/// <para>
/// The gate is never held across an <c>await</c> by this class itself: the body owns the awaits.
/// Nothing here takes the snapshot store's lock, so the two locks cannot deadlock.
/// </para>
/// </summary>
public sealed class StaleRebuildGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Runs <paramref name="body"/> while holding the gate, WAITING for it if busy. Use this for
    /// operations that must happen (explicit reload, document commit).
    ///
    /// <para>
    /// <paramref name="restoreOnIncomplete"/> is invoked when the body throws or is cancelled, and
    /// NOT when it completes. An exception from the body propagates after the restore, so the
    /// caller still sees cancellation — the rebuild layer above decides what to log.
    /// </para>
    /// </summary>
    public async Task RunExclusiveAsync(
        Func<Task> body, Func<Task> restoreOnIncomplete, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await body().ConfigureAwait(false);
        }
        catch
        {
            // Cancelled or failed, not completed: hand the drained work back to the tracker before
            // letting the exception escape, otherwise the pending rebuild is lost for good.
            await restoreOnIncomplete().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Opportunistic form: acquires only if the gate is free, never waits. Returns <c>false</c>
    /// without running the body when another producer holds the gate.
    ///
    /// <para>
    /// <c>WaitAsync(0)</c>, not <c>Wait(0)</c>: a synchronous wait would block a pool thread even
    /// for a zero-timeout probe, defeating the point of the opportunistic path.
    /// </para>
    /// </summary>
    public async Task<bool> TryRunExclusiveAsync(
        Func<Task> body, Func<Task> restoreOnIncomplete, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return false;

        try
        {
            await body().ConfigureAwait(false);
            return true;
        }
        catch
        {
            await restoreOnIncomplete().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Acquires the gate if free. The caller MUST pair a <c>true</c> result with
    /// <see cref="Exit"/> — including on the exception path.
    /// </summary>
    public Task<bool> TryEnterAsync(CancellationToken ct) => _gate.WaitAsync(0, ct);

    /// <summary>Releases a gate acquired by <see cref="TryEnterAsync"/> or by awaiting the gate.</summary>
    public void Exit() => _gate.Release();

    /// <summary>Acquires the gate, waiting if busy. Pair with <see cref="Exit"/>.</summary>
    public Task EnterAsync(CancellationToken ct = default) => _gate.WaitAsync(ct);

    public void Dispose() => _gate.Dispose();
}
