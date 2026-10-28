using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynCodeLens.Analyzers;
using RoslynCodeLens.Metadata;
using RoslynCodeLens.SolutionLifecycle;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens;

/// <summary>
/// The façade over one loaded solution's analysis lifecycle: creation, the readiness-gated
/// snapshot, document commit, explicit reload and disposal. Every responsibility beyond that
/// contract was extracted in stage 5.2 into a collaborator:
///
/// <list type="bullet">
/// <item><see cref="SolutionSnapshotStore"/> — the swappable (solution, resolver, metadata) triple
/// and the lock that makes reading it atomic (invariant #282).</item>
/// <item><see cref="ReadyGate"/> — the readiness protocol: load-failure check, warmup wait,
/// warmup-failure surfacing.</item>
/// <item><see cref="StaleRebuildGate"/> — the single-flight lock plus drain/restore bookkeeping.</item>
/// <item><see cref="StaleRebuildCoordinator"/> — the rebuild engine (incremental vs full reload).
/// </item>
/// <item><see cref="SolutionLifecycleFactory"/> — opening a solution and the explicit reload.</item>
/// <item><see cref="RecentChangesJournal"/> — the changed-documents journal.</item>
/// </list>
///
/// <para>
/// <b>Lock ordering (must not be inverted).</b> <see cref="_lock"/> guards the disposable lifecycle
/// state (<c>_workspace</c>, <c>_analyzerLoader</c>, <c>_disposed</c>). The snapshot store has its
/// own lock. Where both are needed, <see cref="_lock"/> is taken first and the store's lock inside
/// it; nothing takes <see cref="_lock"/> while holding the store's lock. Neither is held across an
/// <c>await</c>.
/// </para>
///
/// <para>
/// <b>Readiness runs outside every lock.</b> The warmup and the stale rebuild both take the
/// snapshot store's lock to publish their result, so running readiness while holding it would
/// self-deadlock. The accessors therefore run readiness first and read the snapshot afterwards.
/// </para>
/// </summary>
public sealed class SolutionManager : IDisposable, ISolutionLifecycleState
{
    private readonly SolutionSnapshotStore _snapshot;
    private readonly ReadyGate _ready;
    private readonly StaleRebuildGate _rebuildGate = new();
    private readonly RecentChangesJournal _recentChanges = new();
    private readonly StaleRebuildCoordinator _rebuild;
    private readonly string? _solutionPath;

    // Guards the disposable lifecycle state only: _workspace, _analyzerLoader, _disposed. Taken
    // before the snapshot store's lock, never after.
    private readonly Lock _lock = new();

    private readonly PEFileCache _peCache = new();
    private readonly IlDisassemblerAdapter _ilAdapter;

    private FileChangeTracker? _tracker;
    private ShadowCopyAnalyzerAssemblyLoader? _analyzerLoader;
    private Workspace? _workspace;
    private bool _disposed;

    private SolutionManager(LoadedSolution loaded, string? solutionPath, Exception? loadFailure = null)
    {
        _solutionPath = solutionPath;
        var resolver = new SymbolResolver(loaded);
        _snapshot = new SolutionSnapshotStore(
            loaded, resolver, new MetadataSymbolResolver(loaded, resolver));
        _ilAdapter = new IlDisassemblerAdapter(_peCache);
        _rebuild = new StaleRebuildCoordinator(_snapshot, _rebuildGate, _recentChanges, this);
        _ready = new ReadyGate(
            solutionPath,
            loadFailure,
            loadFailure == null ? null : SolutionLoadFailure.Describe(solutionPath!, loadFailure));

        // A failed load installs no after-ready hook: every accessor throws before reaching it.
        if (loadFailure == null)
        {
            // The hook must receive the caller's token: a rebuild started with None would be
            // uncancellable, and cancellation must reach the caller as OperationCanceledException.
            _ready.SetAfterReadyHook(_rebuild.RebuildIfStaleAsync);
        }
    }

    public bool HasLoadFailure => _ready.HasLoadFailure;
    public string? LoadFailureMessage => _ready.LoadFailureMessage;

    public static async Task<SolutionManager> CreateAsync(string solutionPath, ProjectFilter? filter = null)
    {
        var open = await SolutionLifecycleFactory.OpenAsync(solutionPath, filter).ConfigureAwait(false);
        if (open.Opened == null)
            return new SolutionManager(LoadedSolution.Empty, solutionPath, open.LoadFailure!);

        var manager = new SolutionManager(open.Opened.Loaded, solutionPath);
        manager._analyzerLoader = open.Opened.AnalyzerLoader;
        manager._workspace = open.Opened.Workspace;
        // CreateAsync stays NON-blocking: the warmup is started here and awaited only on first
        // access, which is pinned by CreateAsync_ReturnsBeforeCompilationCompletes.
        manager._ready.SetWarmup(manager.WarmupAsync(open.Opened));
        return manager;
    }

    public static SolutionManager CreateEmpty()
    {
        return new SolutionManager(LoadedSolution.Empty, null);
    }

    private async Task WarmupAsync(SolutionLifecycleFactory.Opened opened)
    {
        _ready.ResetWarmupCompleted();
        try
        {
            await Console.Error.WriteLineAsync("[roslyn-codelens] Background compilation starting...").ConfigureAwait(false);
            var compilations = await opened.Loader.CompileAllParallelAsync(opened.Solution).ConfigureAwait(false);

            var newLoaded = SolutionLifecycleFactory.CreateLoadedSolution(
                opened.Solution, compilations, opened.Skipped, opened.LoadDiagnostics);
            var newResolver = new SymbolResolver(newLoaded);
            var newMetadataResolver = new MetadataSymbolResolver(newLoaded, newResolver);

            _snapshot.Swap(newLoaded, newResolver, newMetadataResolver);

            if (_solutionPath != null)
            {
                _tracker = new FileChangeTracker(newLoaded, _solutionPath);
                _tracker.OnDllChanged = path => _peCache.Invalidate(path);
            }

            await Console.Error.WriteLineAsync(
                $"[roslyn-codelens] Background compilation complete. {compilations.Count} project(s) compiled.")
                .ConfigureAwait(false);
            _ready.SetWarmup(null);
        }
        catch (Exception ex)
        {
            _ready.RecordWarmupFailure(ex);
            await Console.Error.WriteLineAsync($"[roslyn-codelens] Background compilation failed: {ex}").ConfigureAwait(false);
        }
        finally
        {
            // Release synchronous waiters whatever the outcome; the recorded warmup failure carries
            // the error. Signalled before the task completes, which is fine: the state swap above
            // happens before both.
            _ready.SignalWarmupCompleted();
        }
    }

    public Task WaitForWarmupAsync() => _ready.WarmupTask ?? Task.CompletedTask;

    /// <summary>
    /// The async readiness protocol. Called OUTSIDE the snapshot store's lock on purpose: the
    /// warmup and the stale rebuild both take it internally, so holding it here would deadlock.
    /// </summary>
    private Task EnsureReadyAsync(CancellationToken ct) => _ready.ReadyAsync(ct);

    /// <summary>
    /// The synchronous readiness protocol. <b>[Portion C]</b> — deleted, together with
    /// <see cref="StaleRebuildCoordinator.RebuildIfStale"/>, once every tool awaits the async path.
    /// </summary>
    private void EnsureReady()
    {
        _ready.Ready();
        _rebuild.RebuildIfStale();
    }

    public LoadedSolution GetLoadedSolution()
    {
        EnsureReady();
        return _snapshot.Snapshot().Loaded;
    }

    /// <summary>
    /// The one deliberate exception to the readiness protocol (stage 3, item 5): the IL disassembler
    /// reads NO loaded-solution state — it is built from the <see cref="PEFileCache"/> alone and
    /// disassembles by assembly path + metadata token, so it works identically before load, during
    /// warmup and after a failed load. Routing it through the protocol would buy no correctness and
    /// would REJECT a supported case (<c>peek_il</c> on a closed-source assembly). That asymmetry is
    /// pinned by <c>GetIlDisassembler_OnLoadFailure_DoesNotThrow</c>.
    /// </summary>
    public IlDisassemblerAdapter GetIlDisassembler() => _ilAdapter;

    public void EnsureLoaded()
    {
        EnsureReady();
        var loaded = _snapshot.Snapshot().Loaded;
        if (loaded.IsEmpty)
            throw new InvalidOperationException(DescribeEmptyWorkspace(_solutionPath, loaded.SkippedProjects));
    }

    /// <summary>
    /// An empty workspace has two very different causes and they need different messages. Saying
    /// "No .sln file found" when a solution WAS found but every project failed to open sends the
    /// reader hunting for a missing file instead of at the load failure that actually happened
    /// (issue #399 was reported partly on the strength of this message).
    /// </summary>
    internal static string DescribeEmptyWorkspace(string? solutionPath, IReadOnlyList<SkippedProject> skipped)
    {
        if (solutionPath is null)
        {
            return "No solution found. Either run from a directory containing a .sln/.slnx file, " +
                   "or pass the solution path as argument: roslyn-codelens-mcp /path/to/Solution.sln";
        }

        var name = Path.GetFileName(solutionPath);
        if (skipped.Count == 0)
            return $"Solution '{name}' loaded but contains no projects that could be compiled.";

        var reasons = string.Join("; ", skipped.Take(5).Select(p => $"{p.Name}: {p.Reason}"));
        var more = skipped.Count > 5 ? $" (and {skipped.Count - 5} more)" : "";
        return $"Solution '{name}' loaded but all {skipped.Count} project(s) were skipped, so there is " +
               $"nothing to query. Reasons: {reasons}{more}";
    }

    /// <summary>Test-only seam: an artificial pause inside the stale rebuild.</summary>
    internal void SetRebuildDelayForTest(TimeSpan delay) => _rebuild.SetRebuildDelayForTest(delay);

    /// <summary>Test-only seam: whether the tracker currently holds stale projects.</summary>
    internal bool HasStaleProjectsForTest => _tracker?.HasStaleProjects ?? false;

    /// <summary>
    /// Test-only seam: classify a changed path synchronously (bypassing the watcher's debounce) so
    /// the incremental rebuild path can be exercised deterministically.
    /// </summary>
    internal void SimulateFileChangeForTest(string fullPath) => _tracker?.NotifyChangedPathForTest(fullPath);

    /// <summary>
    /// Commits already-written document texts to the in-memory snapshot immediately, so a query
    /// issued right after a tool wrote files sees the new text instead of waiting out the file
    /// watcher's debounce window. Delegates to the rebuild engine.
    /// </summary>
    public Task CommitDocumentTextsAsync(
        IReadOnlyList<(DocumentId Id, Microsoft.CodeAnalysis.Text.SourceText Text)> documents,
        CancellationToken ct = default)
        => _rebuild.CommitDocumentTextsAsync(documents, ct);

    /// <summary>
    /// Async-first snapshot access: brings the snapshot up to date, then reads the triple under one
    /// lock so it cannot pair a solution with a resolver from a different swap (invariant #282).
    ///
    /// <para>
    /// Readiness runs BEFORE the read, never inside it: warmup and the rebuild both acquire the
    /// store's lock themselves, so reading first would deadlock.
    /// </para>
    /// </summary>
    public async Task<SolutionAnalysisContext> GetAnalysisContextAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        return _snapshot.Snapshot();
    }

    /// <summary>
    /// Synchronous snapshot access, kept for the <c>*Tool</c> implementations not yet converted.
    /// <b>[Portion C]</b> — disappears once every tool awaits <see cref="GetAnalysisContextAsync"/>.
    ///
    /// <para>
    /// ⚠️ Blocks for the whole warmup and, when stale, for the entire rebuild. Semantics are
    /// identical to the async path: same protocol, same single-lock snapshot, and with no stale
    /// projects the very same resolver instance.
    /// </para>
    /// </summary>
    public SolutionAnalysisContext GetAnalysisContext()
    {
        SolutionAnalysisContext context = default;
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);

        _ = Task.Run(async () =>
        {
            try
            {
                context = await GetAnalysisContextAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();

        if (failure != null)
            throw failure;

        return context;
    }

    /// <summary>
    /// Paths of .cs documents changed since the last <see cref="ResetRecentChanges"/> (or since
    /// load). Delegates to <see cref="RecentChangesJournal"/>, which owns the journal and its
    /// synchronization.
    /// </summary>
    public IReadOnlyList<string> RecentlyChangedDocuments => _recentChanges.Snapshot();

    /// <summary>
    /// Clears the <see cref="RecentlyChangedDocuments"/> journal. Called by get_diagnostics with
    /// `resetRecentChanges: true` so the next changedOnly query starts from a clean slate.
    /// </summary>
    public void ResetRecentChanges() => _recentChanges.Reset();

    public async Task<(int ProjectCount, TimeSpan Elapsed)> ForceReloadAsync()
    {
        if (_solutionPath == null)
            throw new InvalidOperationException("No solution path configured. Cannot reload.");

        // Hold the gate for the whole reload: wait for any in-progress auto-rebuild to finish, then
        // keep it out (auto-rebuilds skip and serve current data) so none can clobber our swap or
        // dispose a workspace we still reference.
        var result = default((int ProjectCount, TimeSpan Elapsed));
        await _rebuildGate.RunExclusiveAsync(async () =>
        {
            result = await ReloadAndSwapAsync().ConfigureAwait(false);
        }, restoreOnIncomplete: () => Task.CompletedTask).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Re-opens the solution from disk, recompiles everything, and swaps in the result, disposing
    /// the superseded workspace/loader afterwards. Must be called while holding the rebuild gate.
    /// </summary>
    private async Task<(int ProjectCount, TimeSpan Elapsed)> ReloadAndSwapAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var reload = await SolutionLifecycleFactory.ReloadAsync(_solutionPath!).ConfigureAwait(false);
        var newLoaded = reload.Loaded;
        var newResolver = new SymbolResolver(newLoaded);
        var newMetadataResolver = new MetadataSymbolResolver(newLoaded, newResolver);

        // Snapshot, analyzer loader and workspace all swap in one critical section — a reader must
        // not see the new workspace with the old snapshot.
        ShadowCopyAnalyzerAssemblyLoader? oldLoader;
        Workspace? oldWorkspace;
        lock (_lock)
        {
            _snapshot.Swap(newLoaded, newResolver, newMetadataResolver);
            oldLoader = _analyzerLoader;
            oldWorkspace = _workspace;
            _analyzerLoader = reload.AnalyzerLoader;
            _workspace = reload.Workspace;
        }

        _tracker?.UpdateMappings(newLoaded);
        _tracker?.ClearStale();

        // A full reload recompiled every project from disk — the "changed since last reset" journal
        // is stale by definition and is cleared so the next changedOnly query starts clean.
        ResetRecentChanges();

        // Dispose the previous workspace/loader AFTER the swap so nothing in-flight uses them.
        oldWorkspace?.Dispose();
        oldLoader?.Dispose();

        sw.Stop();
        return (newLoaded.Compilations.Count, sw.Elapsed);
    }

    public void Dispose()
    {
        // Idempotent: snapshot the swappable state under _lock (ForceReloadAsync swaps
        // _workspace/_analyzerLoader under the same lock), then dispose outside it. This avoids a
        // double release of refcounted analyzer handles that could prematurely unload another live
        // solution's analyzer ALC.
        Workspace? workspace;
        ShadowCopyAnalyzerAssemblyLoader? analyzerLoader;
        FileChangeTracker? tracker;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            workspace = _workspace;
            analyzerLoader = _analyzerLoader;
            tracker = _tracker;
            _workspace = null;
            _analyzerLoader = null;
        }
        tracker?.Dispose();
        _peCache.Dispose();
        workspace?.Dispose();
        analyzerLoader?.Dispose();   // releases each acquired analyzer -> refcount--, unload at zero
        _rebuildGate.Dispose();
    }

    // ---- ISolutionLifecycleState: the port the rebuild engine reads through ------------------

    FileChangeTracker? ISolutionLifecycleState.Tracker => _tracker;

    string? ISolutionLifecycleState.SolutionPath => _solutionPath;

    ShadowCopyAnalyzerAssemblyLoader? ISolutionLifecycleState.AnalyzerLoader
    {
        get { lock (_lock) return _analyzerLoader; }
    }

    /// <summary>
    /// Swaps the snapshot and the workspace in one critical section and returns the superseded
    /// workspace so the caller disposes it outside the lock.
    /// </summary>
    Workspace? ISolutionLifecycleState.SwapLifecycleAndSnapshot(
        LoadedSolution loaded,
        SymbolResolver resolver,
        MetadataSymbolResolver metadataResolver,
        Workspace? workspace)
    {
        lock (_lock)
        {
            _snapshot.Swap(loaded, resolver, metadataResolver);
            var oldWorkspace = _workspace;
            _workspace = workspace;
            return oldWorkspace;
        }
    }
}
