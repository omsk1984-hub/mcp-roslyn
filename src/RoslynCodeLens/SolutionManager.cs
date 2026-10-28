using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynCodeLens.Analyzers;
using RoslynCodeLens.Metadata;
using RoslynCodeLens.SolutionLifecycle;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens;

public sealed class SolutionManager : IDisposable
{
    // The analysis snapshot (LoadedSolution + both resolvers) and the lock that makes reading it
    // atomic live in SolutionSnapshotStore (stage 5.2). _lock below still guards the
    // workspace/analyzer-loader lifecycle, which is a different concern with the same lock ordering.
    private readonly SolutionSnapshotStore _snapshot;
    private readonly string? _solutionPath;
    private FileChangeTracker? _tracker;

    // Guards the disposable lifecycle state (_workspace, _analyzerLoader, _disposed). The snapshot
    // triple has its own lock inside SolutionSnapshotStore; neither lock is ever held across an
    // await, and this one is never taken while calling into the store.
    private readonly Lock _lock = new();

    // Journal of .cs document paths changed since the last explicit reset (stage 5.2: extracted
    // into RecentChangesJournal — see that type for the contract and the rationale).
    private readonly RecentChangesJournal _recentChanges = new();

    // Serializes every operation that produces a new loaded solution — the watcher-driven
    // auto-rebuild (RebuildIfStale) and the explicit rebuild_solution (ForceReloadAsync). Only one
    // runs at a time so neither can read _loaded, have the other swap + dispose its workspace, and
    // then clobber the swap with a solution forked off a now-disposed workspace. Auto-rebuild
    // acquires it opportunistically (skips if busy, returning current data); ForceReloadAsync waits.
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private Task? _warmupTask;
    private Exception? _warmupException;

    // Signalled once the background warmup has finished (success or failure). The synchronous
    // readiness path waits on THIS rather than on _warmupTask: waiting on a Task from synchronous
    // code is the sync-over-async pattern stage 4 exists to remove, and find_async_violations
    // flags it inside this file. The async path awaits _warmupTask instead.
    private readonly ManualResetEventSlim _warmupCompleted = new(initialState: true);
    private readonly Exception? _loadException;
    private readonly string? _loadFailureMessage;
    private readonly PEFileCache _peCache = new();
    private readonly IlDisassemblerAdapter _ilAdapter;

    // Process-lifetime shared shadow-copy cache (never disposed): the cross-solution
    // dedup layer. Individual solutions release their entries via their own facade
    // analyzer loader on dispose (refcount--), so entries unload when the last owner goes.
    private static readonly SharedAnalyzerAssemblyCache SharedCache = new();
    private ShadowCopyAnalyzerAssemblyLoader? _analyzerLoader;
    private Workspace? _workspace;
    private bool _disposed;

    private SolutionManager(LoadedSolution loaded, string? solutionPath)
    {
        _solutionPath = solutionPath;
        var resolver = new SymbolResolver(loaded);
        _snapshot = new SolutionSnapshotStore(
            loaded, resolver, new MetadataSymbolResolver(loaded, resolver));
        _ilAdapter = new IlDisassemblerAdapter(_peCache);
    }

    private SolutionManager(string solutionPath, Exception loadException)
    {
        _solutionPath = solutionPath;
        var resolver = new SymbolResolver(LoadedSolution.Empty);
        _snapshot = new SolutionSnapshotStore(
            LoadedSolution.Empty, resolver, new MetadataSymbolResolver(LoadedSolution.Empty, resolver));
        _ilAdapter = new IlDisassemblerAdapter(_peCache);
        _loadException = loadException;
        _loadFailureMessage = SolutionLoadFailure.Describe(solutionPath, loadException);
    }

    public bool HasLoadFailure => _loadException != null;
    public string? LoadFailureMessage => _loadFailureMessage;

    public static async Task<SolutionManager> CreateAsync(string solutionPath, ProjectFilter? filter = null)
    {
        var solutionLoader = new SolutionLoader();
        var analyzerLoader = new ShadowCopyAnalyzerAssemblyLoader(SharedCache);
        SolutionLoader.SolutionOpen open;
        try
        {
            open = await solutionLoader.OpenAsync(solutionPath, filter, analyzerLoader).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            analyzerLoader.Dispose();
            await Console.Error.WriteLineAsync(
                $"[roslyn-codelens] {SolutionLoadFailure.Describe(solutionPath, ex)}").ConfigureAwait(false);
            return new SolutionManager(solutionPath, ex);
        }

        var emptyLoaded = new LoadedSolution
        {
            Solution = open.Solution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation>(),
            SkippedProjects = open.Skipped,
            LoadDiagnostics = open.LoadDiagnostics
        };

        var manager = new SolutionManager(emptyLoaded, solutionPath);
        manager._analyzerLoader = analyzerLoader;
        manager._workspace = open.Workspace;
        manager._warmupTask = manager.WarmupAsync(solutionLoader, open.Solution, open.Skipped, open.LoadDiagnostics);
        return manager;
    }

    public static SolutionManager CreateEmpty()
    {
        return new SolutionManager(LoadedSolution.Empty, null);
    }

    private async Task WarmupAsync(SolutionLoader loader, Solution solution, IReadOnlyList<SkippedProject> skipped, IReadOnlyList<string> loadDiagnostics)
    {
        _warmupCompleted.Reset();
        try
        {
            await Console.Error.WriteLineAsync("[roslyn-codelens] Background compilation starting...").ConfigureAwait(false);
            var compilations = await loader.CompileAllParallelAsync(solution).ConfigureAwait(false);

            var newLoaded = new LoadedSolution
            {
                Solution = solution,
                Compilations = compilations,
                SkippedProjects = skipped,
                LoadDiagnostics = loadDiagnostics
            };
            var newResolver = new SymbolResolver(newLoaded);
            var newMetadataResolver = new MetadataSymbolResolver(newLoaded, newResolver);

            _snapshot.Swap(newLoaded, newResolver, newMetadataResolver);

            if (_solutionPath != null)
            {
                _tracker = new FileChangeTracker(newLoaded, _solutionPath);
                _tracker.OnDllChanged = path => _peCache.Invalidate(path);
            }

            await Console.Error.WriteLineAsync($"[roslyn-codelens] Background compilation complete. {compilations.Count} project(s) compiled.").ConfigureAwait(false);
            _warmupTask = null;
        }
        catch (Exception ex)
        {
            _warmupException = ex;
            await Console.Error.WriteLineAsync($"[roslyn-codelens] Background compilation failed: {ex}").ConfigureAwait(false);
        }
        finally
        {
            // Release the synchronous waiters whatever the outcome; _warmupException carries the
            // failure. Signalled before the warmup task completes, which is fine: the state swap
            // above happens before both.
            _warmupCompleted.Set();
        }
    }

    public Task WaitForWarmupAsync()
    {
        return _warmupTask ?? Task.CompletedTask;
    }

    /// <summary>
    /// The single snapshot-access protocol (stage 3, de-duplicating D1/D3/D4/D6): fail loudly
    /// if the load failed, wait until the background warmup finished, surface a warmup
    /// failure, then bring the snapshot up to date. Every accessor returns state only after
    /// this, so no caller can observe a not-ready manager as if it were ready.
    ///
    /// <para>
    /// This is the async form (stage 4, portion A) and the one new code should use: both waits —
    /// the warmup and the stale rebuild — are awaited, so a multi-second rebuild frees the
    /// calling thread instead of blocking it. The synchronous <see cref="EnsureReady"/> is kept
    /// only as a documented wrapper for the ~51 <c>*Tool</c> implementations that have not been
    /// converted yet (stage 4, portion C).
    /// </para>
    ///
    /// <para>
    /// Called OUTSIDE <see cref="_lock"/> on purpose: warmup and the stale rebuild both take
    /// that lock internally, so holding it here would deadlock. Callers that need several
    /// pieces from the same swap must therefore re-acquire the lock around their read — see
    /// <see cref="GetAnalysisContextAsync"/>, which is the reason this split exists at all.
    /// </para>
    ///
    /// <para>
    /// Note this deliberately does NOT create a fresh <see cref="_resolver"/>: with no stale
    /// projects it must keep returning the cached instance.
    /// </para>
    /// </summary>
    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        ThrowIfLoadFailed();

        // Awaiting (rather than blocking on) the warmup is what keeps this off the caller's thread.
        // The wait itself is still unavoidable on first access: CreateAsync starts warmup in the
        // background and returns immediately (pinned by CreateAsync_ReturnsBeforeCompilationCompletes),
        // so moving the wait into CreateAsync would regress server startup.
        var warmup = Volatile.Read(ref _warmupTask);
        if (warmup != null)
            await warmup.WaitAsync(ct).ConfigureAwait(false);


        if (_warmupException != null)
            throw new InvalidOperationException(WarmupFailedMessage, _warmupException);

        await RebuildIfStaleAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The synchronous form of the readiness protocol, kept for the ~51 <c>*Tool</c> implementations
    /// that have not been converted yet (stage 4, portions A→C).
    ///
    /// <para>
    /// ⚠️ This blocks the calling thread for the whole duration of the background warmup and of a
    /// stale rebuild (potentially seconds on a large solution). New code must await
    /// <see cref="EnsureReadyAsync"/> instead; portion C converts the remaining tools and this
    /// method is expected to disappear then.
    /// </para>
    ///
    /// <para>
    /// It is NOT a wrapper over the async path: the two run the same protocol with their own
    /// wait mechanism — this one blocks on <see cref="_warmupCompleted"/>, the async one awaits
    /// the warmup <see cref="Task"/> — so neither is a synchronous call over an async API.
    /// </para>
    /// </summary>
    private void EnsureReady()
    {
        ThrowIfLoadFailed();

        // Blocks, but only on first access: CreateAsync starts warmup in the background and
        // returns immediately (pinned by CreateAsync_ReturnsBeforeCompilationCompletes), so moving
        // the wait into CreateAsync would regress server startup. Waiting on the event rather than
        // on the Task keeps this out of the sync-over-async pattern (stage 4).
        _warmupCompleted.Wait();
        if (_warmupException != null)
            throw new InvalidOperationException(WarmupFailedMessage, _warmupException);

        RebuildIfStale();
    }

    private const string WarmupFailedMessage = "Solution warmup failed.";

    public LoadedSolution GetLoadedSolution()
    {
        EnsureReady();
        return _snapshot.Snapshot().Loaded;
    }

    // GetResolver / GetMetadataResolver were removed (stage 3, item 4). Their only production
    // consumer was MultiSolutionManager, which now reads the pair from GetAnalysisContext();
    // keeping single-value getters that each run the protocol independently is exactly the
    // redundancy this stage removes. Callers that want the resolver get it from the snapshot.

    /// <summary>
    /// The one deliberate exception to <see cref="EnsureReady"/> (stage 3, item 5).
    ///
    /// <para>
    /// <see cref="IlDisassemblerAdapter"/> reads NO loaded-solution state: it is constructed
    /// from the <see cref="PEFileCache"/> alone and disassembles by assembly path + metadata
    /// token, so it works identically before load, during warmup and after a failed load.
    /// Routing it through the readiness protocol would buy no correctness and would instead
    /// REJECT a supported case — <c>peek_il</c> on a closed-source assembly, which is exactly
    /// the situation where there may be nothing to load at all.
    /// </para>
    ///
    /// <para>
    /// Consequently this accessor neither throws on a failed load nor waits for warmup. That
    /// asymmetry is pinned by <c>GetIlDisassembler_OnLoadFailure_DoesNotThrow</c> and is the
    /// documented reason it stays outside the protocol rather than an oversight.
    /// </para>
    /// </summary>
    public IlDisassemblerAdapter GetIlDisassembler() => _ilAdapter;

    public void EnsureLoaded()
    {
        EnsureReady();
        var loaded = _snapshot.Snapshot().Loaded;
        if (loaded.IsEmpty)
            throw new InvalidOperationException(
                DescribeEmptyWorkspace(_solutionPath, loaded.SkippedProjects));
    }

    /// <summary>
    /// An empty workspace has two very different causes and they need different messages. Saying
    /// "No .sln file found" when a solution WAS found but every project failed to open sends the
    /// reader off hunting for a missing file instead of at the load failure that actually happened
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

    private void ThrowIfLoadFailed()
    {
        if (_loadException != null)
            throw new InvalidOperationException(_loadFailureMessage!, _loadException);
    }

    /// <summary>
    /// Brings the snapshot up to date if the tracker reports stale projects (stage 4, portion A:
    /// the async form of the old blocking <c>RebuildIfStale</c>).
    ///
    /// <para>
    /// Cancellation is handled so that the tracker never loses work: the stale state is
    /// <see cref="FileChangeTracker.DrainStale">drained</see> before the rebuild and
    /// <see cref="FileChangeTracker.RestoreStale">restored</see> if the rebuild does not complete —
    /// whether it failed or was cancelled. An aborted rebuild therefore leaves the projects armed
    /// for the next query instead of silently serving a stale snapshot forever.
    /// </para>
    /// </summary>
    private async Task RebuildIfStaleAsync(CancellationToken ct)
    {
        if (_tracker == null || !_tracker.HasStaleProjects)
            return;

        // Opportunistic acquire: if a rebuild or a full reload (ForceReloadAsync) is already
        // running, skip and let this query return the current consistent data rather than start a
        // competing rebuild. WaitAsync(0) — not Wait(0): a synchronous Wait would block a pool
        // thread even for the zero-timeout opportunistic probe, defeating the point of this path.
        if (!await _rebuildGate.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            await RunRebuildUnderGateAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    /// <summary>
    /// The synchronous twin of <see cref="RebuildIfStaleAsync"/>, retained for the unconverted
    /// <c>*Tool</c> callers (stage 4, portions A→C). Blocks the calling thread until the rebuild
    /// finishes.
    ///
    /// <para>
    /// It is deliberately NOT written as a wrapper calling <c>…Async().GetAwaiter().GetResult()</c>
    /// or <c>Task.Wait()</c>: those are exactly the sync-over-async shapes stage 4 removes, and
    /// they also deadlock or starve the pool under load. Instead it starts the same async core on a
    /// background thread and parks this thread on a <see cref="ManualResetEventSlim"/>, so the
    /// blocking happens on a dedicated thread while the async continuations run normally.
    /// </para>
    ///
    /// <para>
    /// Cancellation cannot apply here (there is no token), and the core's own
    /// <c>catch (Exception)</c> still guarantees a failure is logged rather than thrown — so the
    /// exception is captured only to preserve any cancellation, never surfaced.
    /// </para>
    /// </summary>
    private void RebuildIfStale()
    {
        if (_tracker == null || !_tracker.HasStaleProjects)
            return;

        using var done = new ManualResetEventSlim(false);
        Exception? failure = null;

        _ = Task.Run(async () =>
        {
            try
            {
                await RebuildIfStaleAsync(CancellationToken.None).ConfigureAwait(false);
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

        // The core swallows rebuild failures by design; anything escaping is a real error.
        if (failure != null)
            throw failure;
    }

    /// <summary>
    /// The rebuild body shared by the async path (directly) and the sync path (via
    /// <see cref="RebuildIfStale"/>). Must be called while holding <see cref="_rebuildGate"/>.
    ///
    /// <para>
    /// Drains the stale state, runs the rebuild, and on ANY incomplete exit — failure or
    /// cancellation — restores the drained snapshot so the work is retried rather than lost.
    /// </para>
    /// </summary>
    private async Task RunRebuildUnderGateAsync(CancellationToken ct)
    {
        // Re-check under the gate: another rebuild may have drained the stale state between the
        // pre-check and our acquiring the gate.
        if (_tracker == null || !_tracker.HasStaleProjects)
            return;

        // Drain (snapshot + clear) so edits that land DURING this rebuild accumulate into the
        // freshly-emptied tracker and trigger the next rebuild, instead of being wiped by a
        // blanket clear afterwards (that silently dropped saves made during a multi-second
        // rebuild). On failure we restore the drained state so it retries.
        var snapshot = _tracker.DrainStale();
        var completed = false;
        Console.Error.WriteLine(
            $"[roslyn-codelens] Rebuilding {snapshot.StaleProjectIds.Count} stale project(s)...");

        try
        {
            // Deliberately catch Exception (not just rebuild failures): a background rebuild must
            // never fail the caller's query — it logs and falls back to the cached data. This is
            // pinned by existing behaviour and must not become a rethrow.
            try
            {
                await DelayForTestAsync(ct).ConfigureAwait(false);
                await RebuildAsync(snapshot).ConfigureAwait(false);
                completed = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancelled, not broken: the drained snapshot is restored below (completed is still
                // false), then the cancellation propagates so the caller sees it — Program.cs
                // already surfaces OperationCanceledException as-is.
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[roslyn-codelens] Rebuild failed: {ex}. Using cached data.");
            }
        }
        finally
        {
            if (!completed)
                _tracker?.RestoreStale(snapshot);
        }
    }

    /// <summary>
    /// Test-only seam: an artificial pause inside <see cref="RebuildIfStaleAsync"/>, so a test can
    /// observe the caller WHILE the rebuild is still running. Zero (the production default) makes
    /// <see cref="DelayForTestAsync"/> a completed task, so nothing changes on the hot path.
    /// </summary>
    private TimeSpan _rebuildDelayForTest;

    internal void SetRebuildDelayForTest(TimeSpan delay) => _rebuildDelayForTest = delay;

    /// <summary>Test-only seam: whether the tracker currently holds stale projects.</summary>
    internal bool HasStaleProjectsForTest => _tracker?.HasStaleProjects ?? false;

    private Task DelayForTestAsync(CancellationToken ct) =>
        _rebuildDelayForTest > TimeSpan.Zero ? Task.Delay(_rebuildDelayForTest, ct) : Task.CompletedTask;

    private async Task RebuildAsync(FileChangeTracker.StaleSnapshot snapshot)
    {
        // A pure .cs edit is applied to the existing Solution in place, preserving every
        // ProjectId/DocumentId and keeping the recompiled symbols identity-consistent with
        // loaded.Solution. Re-opening the solution (the #282 bug) minted fresh ProjectIds
        // that no longer matched the tracker's stale ids or the retained compilations, so
        // no project actually recompiled and SymbolFinder silently returned empty. Structural
        // changes (project files, new files) still need MSBuild re-evaluation via a full reload.
        if (!snapshot.RequiresFullReload
            && await TryRebuildIncrementalAsync(snapshot.StaleProjectIds, snapshot.ChangedDocumentPaths).ConfigureAwait(false))
        {
            return;
        }

        await RebuildViaFullReloadAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the changed source files' current on-disk text to the existing solution via
    /// <see cref="Solution.WithDocumentText(DocumentId, Microsoft.CodeAnalysis.Text.SourceText, PreservationMode)"/>,
    /// then recompiles only the stale projects. Because the solution snapshot lineage is
    /// preserved, the stale ids still resolve, unchanged projects reuse their carried-over
    /// compilations, and the resulting symbols stay valid against the same solution — so
    /// <c>SymbolFinder</c> works. Returns false (escalate to a full reload) if a changed file
    /// can't be read or isn't a document in the current solution.
    /// </summary>
    private async Task<bool> TryRebuildIncrementalAsync(
        IReadOnlySet<ProjectId> staleIds, IReadOnlyList<string> changedDocumentPaths)
    {
        var solution = _snapshot.Snapshot().Loaded.Solution;

        foreach (var path in changedDocumentPaths)
        {
            var docIds = solution.GetDocumentIdsWithFilePath(path);
            if (docIds.IsDefaultOrEmpty)
                return false; // no longer a known document — reload so the graph re-evaluates

            var text = await TryReadSourceTextAsync(path).ConfigureAwait(false);
            if (text == null)
                return false; // deleted or unreadable — reload

            foreach (var docId in docIds)
                solution = solution.WithDocumentText(docId, text);

            RecordRecentlyChanged(path);
        }

        await RecompileAndSwapAsync(solution, staleIds).ConfigureAwait(false);

        await Console.Error.WriteLineAsync("[roslyn-codelens] Rebuild complete (incremental).").ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Commits already-written document texts to the in-memory snapshot immediately, so queries
    /// issued right after a tool wrote files (rename_symbol apply) see the new text instead of
    /// waiting out the file watcher's debounce window. Serialized with the watcher auto-rebuild
    /// and ForceReloadAsync via <see cref="_rebuildGate"/> (waits, like ForceReloadAsync does),
    /// then reuses the incremental-rebuild core: <c>WithDocumentText</c> per document, recompile
    /// the changed projects plus their transitive dependents (mirroring the watcher's
    /// transitive staleness), swap the snapshot, and re-map the tracker. Document ids that no
    /// longer exist (a full reload swapped in fresh ids mid-flight) are skipped — that reload
    /// already read the committed text from disk.
    /// </summary>
    public async Task CommitDocumentTextsAsync(
        IReadOnlyList<(DocumentId Id, Microsoft.CodeAnalysis.Text.SourceText Text)> documents,
        CancellationToken ct = default)
    {
        if (documents.Count == 0)
            return;

        await _rebuildGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var solution = _snapshot.Snapshot().Loaded.Solution;
            var changedProjects = new HashSet<ProjectId>();
            foreach (var (docId, text) in documents)
            {
                if (solution.GetDocument(docId) == null)
                    continue;
                solution = solution.WithDocumentText(docId, text);
                changedProjects.Add(docId.ProjectId);

                var doc = solution.GetDocument(docId);
                if (doc?.FilePath != null)
                    RecordRecentlyChanged(doc.FilePath);
            }

            if (changedProjects.Count == 0)
                return;

            var staleIds = new HashSet<ProjectId>(changedProjects);
            var graph = solution.GetProjectDependencyGraph();
            foreach (var projectId in changedProjects)
                staleIds.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject(projectId));

            await RecompileAndSwapAsync(solution, staleIds).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(
                $"[roslyn-codelens] Committed {documents.Count} written document(s) to the in-memory snapshot.")
                .ConfigureAwait(false);
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    /// <summary>
    /// Shared tail of the in-place update paths (#282 pattern): recompiles the stale projects of
    /// an already-updated solution fork (unchanged projects keep their carried-over compilations,
    /// preserving symbol identity), swaps in the new LoadedSolution + resolvers through
    /// <see cref="SolutionSnapshotStore.Swap"/> (one lock, all three parts — invariant #282), and
    /// re-maps the tracker so future edits resolve against the new authoritative snapshot.
    /// Callers must hold <see cref="_rebuildGate"/>.
    /// </summary>
    private async Task RecompileAndSwapAsync(Solution solution, IReadOnlySet<ProjectId> staleIds)
    {
        var previous = _snapshot.Snapshot().Loaded;
        var compilations = new ConcurrentDictionary<ProjectId, Compilation>(previous.Compilations);
        var staleProjects = solution.Projects.Where(p => staleIds.Contains(p.Id)).ToList();
        var tasks = staleProjects.Select(async project =>
        {
            await Console.Error.WriteLineAsync($"[roslyn-codelens] Recompiling: {project.Name}").ConfigureAwait(false);
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation != null)
                compilations[project.Id] = compilation;
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        var newLoaded = SolutionSnapshotStore.CreateLoadedSolution(
            solution, compilations, previous.SkippedProjects, previous.LoadDiagnostics);
        var newResolver = new SymbolResolver(newLoaded);
        var newMetadataResolver = new MetadataSymbolResolver(newLoaded, newResolver);

        _snapshot.Swap(newLoaded, newResolver, newMetadataResolver);

        // Re-map file->project/document lookups against the new snapshot. Ids are preserved,
        // but a fresh snapshot instance is now authoritative and future edits resolve against it.
        _tracker?.UpdateMappings(newLoaded);
    }

    /// <summary>
    /// Re-opens the solution from disk and recompiles every project. Used for structural
    /// changes (project files, added/removed files) that require MSBuild re-evaluation, and
    /// as the escalation path when an incremental edit can't be applied. Recompiling all
    /// projects is required for correctness: a freshly opened solution has new ProjectIds, so
    /// the old compilations cannot be mixed with it (mixing them was the #282 silent-empty bug).
    /// </summary>
    private async Task RebuildViaFullReloadAsync()
    {
        var solutionLoader = new SolutionLoader();

        // Reuse this solution's existing shadow-copy analyzer loader so generators load from
        // shadow copies (issue #254) instead of re-locking bin\Debug, and the returned solution
        // stays remapped. Snapshot under _lock in case a concurrent reload swapped it. Do NOT
        // dispose it here: its already-acquired analyzers are shared via the process-wide cache.
        ShadowCopyAnalyzerAssemblyLoader? analyzerLoader;
        lock (_lock) { analyzerLoader = _analyzerLoader; }

        var open = await solutionLoader.OpenAsync(_solutionPath!, null, analyzerLoader).ConfigureAwait(false);
        var solution = open.Solution;
        var workspace = open.Workspace;
        var compilations = await solutionLoader.CompileAllParallelAsync(solution).ConfigureAwait(false);

        var newLoaded = SolutionSnapshotStore.CreateLoadedSolution(
            solution, compilations, open.Skipped, open.LoadDiagnostics);
        var newResolver = new SymbolResolver(newLoaded);
        var newMetadataResolver = new MetadataSymbolResolver(newLoaded, newResolver);

        // Swap the snapshot and the workspace inside one critical section. The two live under
        // different locks (the store's and this one), so taking _lock first keeps the original
        // ordering: no reader can see the new workspace paired with the old snapshot. _lock is
        // never taken while holding the store's lock, so this cannot deadlock.
        Workspace? oldWorkspace;
        lock (_lock)
        {
            _snapshot.Swap(newLoaded, newResolver, newMetadataResolver);
            oldWorkspace = _workspace;
            _workspace = workspace;
        }

        _tracker!.UpdateMappings(newLoaded);

        // Dispose the previous workspace AFTER the swap (fixes a pre-existing leak: each stale rebuild
        // otherwise leaks a workspace + its out-of-process BuildHost). The analyzer loader is REUSED, not disposed.
        oldWorkspace?.Dispose();

        await Console.Error.WriteLineAsync("[roslyn-codelens] Rebuild complete.").ConfigureAwait(false);
    }

    private static async Task<Microsoft.CodeAnalysis.Text.SourceText?> TryReadSourceTextAsync(string path)
    {
        // The file may still be locked by the editor that just wrote it; a couple of short
        // retries clear that. A genuine failure (deleted, access denied) returns null so the
        // caller escalates to a full reload rather than applying stale text.
        const int attempts = 3;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Microsoft.CodeAnalysis.Text.SourceText.From(stream);
            }
            catch (IOException) when (attempt < attempts - 1)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Test-only seam: classify a changed path synchronously (bypassing the file watcher's
    /// debounce) so the incremental rebuild path can be exercised deterministically.
    /// </summary>
    internal void SimulateFileChangeForTest(string fullPath) => _tracker?.NotifyChangedPathForTest(fullPath);

    /// <summary>
    /// Async-first snapshot access (stage 4, portion A): rebuilds if stale, then snapshots the
    /// loaded solution and both resolvers under one lock so they are guaranteed to come from the
    /// same swap. Awaiting the readiness wait — rather than blocking on it — is what keeps a
    /// multi-second stale rebuild off the caller's thread.
    ///
    /// <para>
    /// The single-snapshot guarantee (invariant #282) is unchanged: the three fields are read in
    /// one <c>lock</c> block, so a watcher-driven swap cannot pair a solution with a resolver from
    /// a different snapshot.
    /// </para>
    ///
    /// <para>
    /// <see cref="EnsureReadyAsync"/> runs BEFORE the lock is taken, never inside it: warmup and
    /// the stale rebuild both acquire <see cref="_lock"/> themselves, so locking first would deadlock.
    /// </para>
    /// </summary>
    public async Task<SolutionAnalysisContext> GetAnalysisContextAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        return _snapshot.Snapshot();
    }

    /// <summary>
    /// Synchronous snapshot access, kept for the ~51 <c>*Tool</c> implementations that have not been
    /// converted yet (stage 4, portions A→C).
    ///
    /// <para>
    /// ⚠️ Documented wrapper: it blocks the calling thread until the background warmup finishes and,
    /// when projects are stale, for the entire rebuild — seconds on a large solution. New code must
    /// await <see cref="GetAnalysisContextAsync"/>; portion C converts the remaining tools and this
    /// overload is expected to disappear then.
    /// </para>
    ///
    /// <para>
    /// Semantics are identical to the async path: same readiness protocol, same single-lock
    /// snapshot, and with no stale projects the very same <see cref="SymbolResolver"/> instance.
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
    /// Paths of .cs documents changed since the last <see cref="ResetRecentChanges"/>
    /// (or since load). Delegates to <see cref="RecentChangesJournal"/>, which owns the journal
    /// and its synchronization (stage 5.2).
    /// </summary>
    public IReadOnlyList<string> RecentlyChangedDocuments => _recentChanges.Snapshot();

    /// <summary>
    /// Clears the <see cref="RecentlyChangedDocuments"/> journal. Called by
    /// get_diagnostics with `resetRecentChanges: true` so the next changedOnly
    /// query starts from a clean slate.
    /// </summary>
    public void ResetRecentChanges() => _recentChanges.Reset();

    private void RecordRecentlyChanged(string path) => _recentChanges.Record(path);

    public async Task<(int ProjectCount, TimeSpan Elapsed)> ForceReloadAsync()
    {
        if (_solutionPath == null)
            throw new InvalidOperationException("No solution path configured. Cannot reload.");

        // Hold the rebuild gate for the whole reload: wait for any in-progress auto-rebuild to
        // finish, then keep it out (auto-rebuilds skip and serve current data) so none can clobber
        // our swap or dispose a workspace we still reference.
        await _rebuildGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ReloadAndSwapAsync().ConfigureAwait(false);
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    /// <summary>
    /// Re-opens the solution from disk, recompiles everything, and swaps in the result, disposing
    /// the superseded workspace/loader afterwards. Must be called while holding <see cref="_rebuildGate"/>.
    /// </summary>
    private async Task<(int ProjectCount, TimeSpan Elapsed)> ReloadAndSwapAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var solutionLoader = new SolutionLoader();
        var analyzerLoader = new ShadowCopyAnalyzerAssemblyLoader(SharedCache);
        SolutionLoader.SolutionOpen open;
        try
        {
            open = await solutionLoader.OpenAsync(_solutionPath!, null, analyzerLoader).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            analyzerLoader.Dispose();
            throw new InvalidOperationException(SolutionLoadFailure.Describe(_solutionPath!, ex), ex);
        }
        var solution = open.Solution;
        var workspace = open.Workspace;
        var compilations = await solutionLoader.CompileAllParallelAsync(solution).ConfigureAwait(false);

        var newLoaded = SolutionSnapshotStore.CreateLoadedSolution(
            solution, compilations, open.Skipped, open.LoadDiagnostics);
        var newResolver = new SymbolResolver(newLoaded);
        var newMetadataResolver = new MetadataSymbolResolver(newLoaded, newResolver);

        // Snapshot, analyzer loader and workspace all swap in one critical section, exactly as
        // before the extraction — a reader must not see the new workspace with the old snapshot.
        ShadowCopyAnalyzerAssemblyLoader? oldLoader;
        Workspace? oldWorkspace;
        lock (_lock)
        {
            _snapshot.Swap(newLoaded, newResolver, newMetadataResolver);
            oldLoader = _analyzerLoader;
            oldWorkspace = _workspace;
            _analyzerLoader = analyzerLoader;
            _workspace = workspace;
        }

        _tracker?.UpdateMappings(newLoaded);
        _tracker?.ClearStale();

        // A full reload recompiled every project from disk — the "changed since last reset"
        // journal is stale by definition and is cleared so the next changedOnly query
        // starts from a clean slate.
        ResetRecentChanges();

        // Dispose the previous workspace/loader AFTER the state swap so nothing in-flight uses them.
        oldWorkspace?.Dispose();
        oldLoader?.Dispose();

        sw.Stop();
        return (newLoaded.Compilations.Count, sw.Elapsed);
    }

    public void Dispose()
    {
        // Idempotent: snapshot the swappable state under _lock (ForceReloadAsync swaps
        // _workspace/_analyzerLoader under the same lock), then dispose outside it. This
        // avoids a double release of refcounted analyzer handles that could prematurely
        // unload another live solution's analyzer ALC.
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
    }
}
