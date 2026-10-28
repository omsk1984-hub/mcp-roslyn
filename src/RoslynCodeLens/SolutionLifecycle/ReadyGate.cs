namespace RoslynCodeLens.SolutionLifecycle;

/// <summary>
/// The single readiness protocol for reading a solution's analysis state. Extracted from
/// <see cref="SolutionManager"/> (stage 5.2, item 4): fail loudly if the load failed, wait until the
/// background warmup finished, surface a warmup failure, then run the after-ready step (the stale
/// rebuild). Every accessor returns state only after this, so no caller can observe a not-ready
/// manager as if it were ready.
///
/// <para>
/// <b>Why this is last and why it is split from portion C.</b> The class carries TWO halves:
/// </para>
/// <list type="bullet">
/// <item><b>Permanent</b> — <see cref="ReadyAsync"/> plus the load-failure state
/// (<see cref="HasLoadFailure"/>, <see cref="LoadFailureMessage"/>). This is the protocol the
/// manager's async accessors use, and it survives.</item>
/// <item><b>Portion C</b> — <see cref="Ready"/> and the synchronous warmup event
/// (<see cref="SignalWarmupCompleted"/> / the wait inside <see cref="Ready"/>). Stage 4's portion C
/// converts the remaining <c>*Tool</c> implementations to async and then deletes these; they exist
/// only so the still-synchronous tools keep working. Keeping them together here means portion C
/// deletes them from one place instead of from the manager.</item>
/// </list>
///
/// <para>
/// <b>The deadlock rule.</b> This type holds NO lock. Warmup and the stale rebuild both take the
/// snapshot store's lock to publish their result, so a caller that held that lock across this
/// protocol would self-deadlock. That is why the manager runs readiness FIRST and only then reads
/// the snapshot — the ordering is the invariant, and confining the protocol to a lock-free type
/// makes violating it a visible design change rather than an easy mistake.
/// </para>
///
/// <para>
/// This type creates no resolvers and no snapshots: with no stale projects the cached instances
/// must be returned untouched, so rebuilding belongs to the rebuild layer, not here.
/// </para>
/// </summary>
public sealed class ReadyGate
{
    private const string WarmupFailedMessage = "Solution warmup failed.";

    private readonly Exception? _loadException;
    private readonly string? _loadFailureMessage;

    // Signalled once the background warmup has finished (success or failure). The SYNCHRONOUS path
    // waits on this rather than on the task: waiting on a Task from synchronous code is the
    // sync-over-async pattern stage 4 removes. The async path awaits the task instead.
    // [Portion C] deleted together with Ready().
    private readonly ManualResetEventSlim _warmupCompleted = new(initialState: true);

    private Task? _warmupTask;
    private Exception? _warmupException;

    // The stale rebuild, injected by the manager so this protocol stays a pure ordering concern.
    // Takes the caller's token: dropping it would silently turn a cancellable rebuild into an
    // uncancellable one, and cancellation must reach the caller as OperationCanceledException.
    private Func<CancellationToken, Task>? _afterReady;

    public ReadyGate(string? solutionPath, Exception? loadException, string? loadFailureMessage)
    {
        SolutionPath = solutionPath;
        _loadException = loadException;
        _loadFailureMessage = loadFailureMessage;
    }

    /// <summary>The solution path this gate belongs to, or null for a path-less (empty) manager.</summary>
    public string? SolutionPath { get; }

    /// <summary>True when the solution failed to load; every accessor must then throw.</summary>
    public bool HasLoadFailure => _loadException != null;

    /// <summary>The original failure description, or null when the load succeeded.</summary>
    public string? LoadFailureMessage => _loadFailureMessage;

    /// <summary>
    /// The background warmup task, or null when there is none. <c>WaitForWarmupAsync</c> exposes
    /// this so a caller (and the tests) can await the warmup without going through the protocol.
    /// </summary>
    public Task? WarmupTask => Volatile.Read(ref _warmupTask);

    /// <summary>Sets the warmup task created by the manager's <c>CreateAsync</c>.</summary>
    public void SetWarmup(Task? warmupTask) => Volatile.Write(ref _warmupTask, warmupTask);

    /// <summary>Records a warmup failure, later surfaced by <see cref="ReadyAsync"/>.</summary>
    public void RecordWarmupFailure(Exception exception) => _warmupException = exception;

    /// <summary>Registers the after-ready step (the stale rebuild).</summary>
    public void SetAfterReadyHook(Func<CancellationToken, Task> afterReady) => _afterReady = afterReady;

    /// <summary>Resets the synchronous warmup event before a warmup starts. [Portion C]</summary>
    public void ResetWarmupCompleted() => _warmupCompleted.Reset();

    /// <summary>Releases synchronous waiters once the warmup finished. [Portion C]</summary>
    public void SignalWarmupCompleted() => _warmupCompleted.Set();

    /// <summary>
    /// The async readiness protocol — the form new code must use. Awaits (never blocks on) the
    /// warmup, so a multi-second wait frees the calling thread.
    ///
    /// <para>
    /// Call this OUTSIDE any lock: the warmup and the after-ready rebuild both acquire the snapshot
    /// store's lock internally.
    /// </para>
    /// </summary>
    public async Task ReadyAsync(CancellationToken ct)
    {
        ThrowIfLoadFailed();

        // Awaiting the warmup (rather than blocking) is what keeps this off the caller's thread.
        // The wait is still unavoidable on first access: CreateAsync starts the warmup in the
        // background and returns immediately, so moving the wait there would regress startup.
        var warmup = Volatile.Read(ref _warmupTask);
        if (warmup != null)
            await warmup.WaitAsync(ct).ConfigureAwait(false);

        if (_warmupException != null)
            throw new InvalidOperationException(WarmupFailedMessage, _warmupException);

        var afterReady = _afterReady;
        if (afterReady != null)
            await afterReady(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The synchronous readiness protocol, kept for the <c>*Tool</c> implementations that have not
    /// been converted yet. <b>[Portion C]</b> — deleted once every tool awaits
    /// <see cref="ReadyAsync"/>.
    ///
    /// <para>
    /// ⚠️ It blocks the calling thread for the whole warmup and for a stale rebuild (potentially
    /// seconds on a large solution). Deliberately NOT a wrapper over <see cref="ReadyAsync"/>:
    /// blocking on the event rather than on the task keeps it out of the sync-over-async pattern.
    /// </para>
    /// </summary>
    public void Ready()
    {
        ThrowIfLoadFailed();

        _warmupCompleted.Wait();

        if (_warmupException != null)
            throw new InvalidOperationException(WarmupFailedMessage, _warmupException);

        // Deliberately does NOT run _afterReady: the synchronous caller uses the BLOCKING rebuild
        // twin, which the manager invokes itself. Running the async hook here would turn this into
        // a blocking call over an async API — the very pattern stage 4 removes.
    }

    /// <summary>
    /// Raises <see cref="InvalidOperationException"/> carrying the original load-failure message
    /// when the solution failed to load — the behaviour every accessor's characteristic test pins.
    /// </summary>
    public void ThrowIfLoadFailed()
    {
        if (_loadException != null)
            throw new InvalidOperationException(_loadFailureMessage!, _loadException);
    }
}
