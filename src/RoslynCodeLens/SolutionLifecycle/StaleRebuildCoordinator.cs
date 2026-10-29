using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynCodeLens.Analyzers;
using RoslynCodeLens.Metadata;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.SolutionLifecycle;

/// <summary>
/// The rebuild engine: decides HOW a stale snapshot is brought up to date (incrementally, or by a
/// full MSBuild reload when the change is structural), and commits the result through
/// <see cref="SolutionSnapshotStore.Swap"/>. Extracted from <see cref="SolutionManager"/> (stage 5.2)
/// because the engine — not the manager's contract — was the bulk of its size and complexity.
///
/// <para>
/// <b>Division of labour.</b> This type does not decide WHETHER to rebuild (that is
/// <see cref="ReadyGate"/>'s protocol, driven by an accessor call) and does not arbitrate WHO
/// rebuilds (that is <see cref="StaleRebuildGate"/>). It owns only the drain/rebuild/restore
/// sequence and the two ways of producing a fresh snapshot.
/// </para>
///
/// <para>
/// <b>Invariant #282.</b> A pure .cs edit is applied to the EXISTING solution via
/// <c>WithDocumentText</c>, so every ProjectId/DocumentId survives and the recompiled symbols stay
/// identity-consistent with the retained compilations. Re-opening the solution for an edit is the
/// original #282 bug: fresh ProjectIds no longer matched the tracker's stale ids, no project
/// recompiled, and <c>SymbolFinder</c> silently returned empty. Structural changes (project files,
/// added/removed files) genuinely need MSBuild re-evaluation and take the full-reload path, which
/// recompiles everything precisely because mixing old compilations with a fresh solution is the
/// same bug in another form.
/// </para>
///
/// <para>
/// <b>Drain/restore.</b> The tracker's stale state is drained before the rebuild and restored if
/// the rebuild does not complete — failed or cancelled — so an aborted rebuild retries instead of
/// leaving the solution stale forever.
/// </para>
/// </summary>
public sealed class StaleRebuildCoordinator
{
    private readonly SolutionSnapshotStore _snapshot;
    private readonly StaleRebuildGate _gate;
    private readonly RecentChangesJournal _journal;
    private readonly ISolutionLifecycleState _state;

    /// <summary>
    /// Test-only pause inside the rebuild, so a test can observe the caller WHILE the rebuild is
    /// still running. Zero (the production default) makes <see cref="DelayForTestAsync"/> a
    /// completed task, so nothing changes on the hot path.
    /// </summary>
    private TimeSpan _rebuildDelayForTest;

    public StaleRebuildCoordinator(
        SolutionSnapshotStore snapshot,
        StaleRebuildGate gate,
        RecentChangesJournal journal,
        ISolutionLifecycleState state)
    {
        _snapshot = snapshot;
        _gate = gate;
        _journal = journal;
        _state = state;
    }

    /// <summary>Sets the test-only rebuild delay.</summary>
    internal void SetRebuildDelayForTest(TimeSpan delay) => _rebuildDelayForTest = delay;

    /// <summary>
    /// Opportunistic stale rebuild: skips without waiting when another producer holds the gate, so
    /// a query serves the current consistent snapshot instead of queueing behind a long rebuild.
    /// </summary>
    public async Task RebuildIfStaleAsync(CancellationToken ct)
    {
        if (_state.Tracker == null || !_state.Tracker.HasStaleProjects)
            return;

        // RunRebuildUnderGateAsync performs its own drain/restore, so the gate's restore hook is a
        // no-op here — passing the restore twice would double-add to the tracker's stale set.
        await _gate.TryRunExclusiveAsync(
            () => RunRebuildUnderGateAsync(ct),
            restoreOnIncomplete: () => Task.CompletedTask,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The blocking twin of <see cref="RebuildIfStaleAsync"/>. <b>[Portion C]</b> — deleted once
    /// every <c>*Tool</c> awaits the async path.
    ///
    /// <para>
    /// Deliberately NOT <c>…Async().GetAwaiter().GetResult()</c> or <c>Task.Wait()</c>: those are
    /// the sync-over-async shapes stage 4 removes and they starve the pool. The async core runs on
    /// a background thread while this thread parks on an event, so blocking happens off the pool.
    /// </para>
    /// </summary>
    public void RebuildIfStale()
    {
        if (_state.Tracker == null || !_state.Tracker.HasStaleProjects)
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
    /// The rebuild body. Must be called while holding the gate.
    ///
    /// <para>
    /// Re-checks staleness under the gate (another rebuild may have drained it between the
    /// pre-check and the acquire), drains, rebuilds, and restores on any incomplete exit.
    /// </para>
    /// </summary>
    private async Task RunRebuildUnderGateAsync(CancellationToken ct)
    {
        var tracker = _state.Tracker;
        if (tracker == null || !tracker.HasStaleProjects)
            return;

        // Drain (snapshot + clear) so edits landing DURING the rebuild accumulate into the freshly
        // emptied tracker and trigger the next rebuild, rather than being wiped by a blanket clear
        // afterwards — that silently dropped saves made during a multi-second rebuild.
        var snapshot = tracker.DrainStale();
        var completed = false;
        Console.Error.WriteLine(
            $"[roslyn-codelens] Rebuilding {snapshot.StaleProjectIds.Count} stale project(s)...");

        try
        {
            try
            {
                // Deliberately catch Exception (not just rebuild failures): a background rebuild
                // must never fail the caller's query — it logs and falls back to cached data. This
                // is pinned by existing behaviour and must not become a rethrow.
                await DelayForTestAsync(ct).ConfigureAwait(false);
                await RebuildAsync(snapshot).ConfigureAwait(false);
                completed = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancelled, not broken: the drained snapshot is restored below (completed is still
                // false), then cancellation propagates so the caller sees it.
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
                tracker.RestoreStale(snapshot);
        }
    }

    private Task DelayForTestAsync(CancellationToken ct) =>
        _rebuildDelayForTest > TimeSpan.Zero ? Task.Delay(_rebuildDelayForTest, ct) : Task.CompletedTask;

    /// <summary>
    /// Routes a drained snapshot to the incremental path, or escalates to a full reload when the
    /// change is structural or the incremental application cannot be done.
    /// </summary>
    private async Task RebuildAsync(FileChangeTracker.StaleSnapshot snapshot)
    {
        if (!snapshot.RequiresFullReload
            && await TryRebuildIncrementalAsync(snapshot.StaleProjectIds, snapshot.ChangedDocumentPaths).ConfigureAwait(false))
        {
            return;
        }

        await RebuildViaFullReloadAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the changed files' current on-disk text to the existing solution, then recompiles
    /// only the stale projects. Because the solution lineage is preserved, stale ids still resolve,
    /// unchanged projects reuse their carried-over compilations, and symbols stay valid against the
    /// same solution — so <c>SymbolFinder</c> works. Returns false (escalate to full reload) if a
    /// changed file cannot be read or is no longer a document in the current solution.
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

            _journal.Record(path);
        }

        await RecompileAndSwapAsync(solution, staleIds).ConfigureAwait(false);

        await Console.Error.WriteLineAsync("[roslyn-codelens] Rebuild complete (incremental).").ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Commits already-written document texts immediately, so a query right after a tool wrote files
    /// sees the new text instead of waiting out the watcher's debounce. Serialized with the
    /// watcher rebuild and the explicit reload through the gate, then reuses the incremental core.
    /// Document ids that no longer exist (a full reload swapped in fresh ids mid-flight) are
    /// skipped — that reload already read the committed text from disk.
    /// </summary>
    public async Task CommitDocumentTextsAsync(
        IReadOnlyList<(DocumentId Id, Microsoft.CodeAnalysis.Text.SourceText Text)> documents,
        CancellationToken ct = default)
    {
        if (documents.Count == 0)
            return;

        // No drained state here, so nothing needs restoring on failure: the committed texts are on
        // disk already and the watcher will pick them up.
        await _gate.RunExclusiveAsync(async () =>
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
                    _journal.Record(doc.FilePath);
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
        }, restoreOnIncomplete: () => Task.CompletedTask, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared tail of the in-place update paths (#282 pattern): recompiles the stale projects of an
    /// already-updated solution fork (unchanged projects keep their carried-over compilations,
    /// preserving symbol identity), swaps the triple through <see cref="SolutionSnapshotStore.Swap"/>
    /// (one lock, all three parts), and re-maps the tracker. Callers must hold the gate.
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

        // Ids are preserved, but a fresh snapshot instance is now authoritative.
        _state.Tracker?.UpdateMappings(newLoaded);
    }

    /// <summary>
    /// Re-opens the solution from disk and recompiles every project. Used for structural changes
    /// (project files, added/removed files) and as the escalation path when an incremental edit
    /// cannot be applied. Recompiling all projects is required for correctness: a freshly opened
    /// solution has new ProjectIds, so old compilations cannot be mixed with it (the #282 bug).
    /// </summary>
    private async Task RebuildViaFullReloadAsync()
    {
        if (_state.SolutionPath == null)
            throw new InvalidOperationException("No solution path configured. Cannot rebuild.");

        var solutionLoader = new SolutionLoader();

        // Reuse this solution's existing shadow-copy analyzer loader so generators load from shadow
        // copies (issue #254) instead of re-locking bin\Debug. Read under one critical section in
        // case a concurrent reload swapped it. Do NOT dispose it: acquired analyzers are shared via
        // the process-wide cache.
        var analyzerLoader = _state.AnalyzerLoader;

        var open = await solutionLoader.OpenAsync(_state.SolutionPath, null, analyzerLoader).ConfigureAwait(false);
        var solution = open.Solution;
        var compilations = await solutionLoader.CompileAllParallelAsync(solution).ConfigureAwait(false);

        var newLoaded = SolutionSnapshotStore.CreateLoadedSolution(
            solution, compilations, open.Skipped, open.LoadDiagnostics);
        var newResolver = new SymbolResolver(newLoaded);
        var newMetadataResolver = new MetadataSymbolResolver(newLoaded, newResolver);

        // Snapshot and workspace swap in one critical section so no reader sees the new workspace
        // paired with the old snapshot. The lifecycle state owns both under its own lock.
        var oldWorkspace = _state.SwapLifecycleAndSnapshot(
            newLoaded, newResolver, newMetadataResolver, open.Workspace);

        _state.Tracker!.UpdateMappings(newLoaded);

        // Dispose the previous workspace AFTER the swap (fixes a leak: each stale rebuild otherwise
        // leaks a workspace and its out-of-process BuildHost). The analyzer loader is reused.
        oldWorkspace?.Dispose();

        await Console.Error.WriteLineAsync("[roslyn-codelens] Rebuild complete.").ConfigureAwait(false);
    }

    private static async Task<Microsoft.CodeAnalysis.Text.SourceText?> TryReadSourceTextAsync(string path)
    {
        // The file may still be locked by the editor that just wrote it; a couple of short retries
        // clear that. A genuine failure (deleted, access denied) returns null so the caller
        // escalates to a full reload rather than applying stale text.
        const int attempts = 3;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
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
}

/// <summary>
/// The lifecycle state the rebuild engine needs but does not own: the tracker, the solution path,
/// the current analyzer loader, and the ability to swap the workspace together with the snapshot.
/// Implemented by <see cref="SolutionManager"/>; extracted as a port so the engine has no back
/// reference to the manager and the manager's public contract stays free of engine details.
/// </summary>
public interface ISolutionLifecycleState
{
    /// <summary>The file-change tracker, or null before a solution path is known.</summary>
    FileChangeTracker? Tracker { get; }

    /// <summary>The solution path, or null for a path-less (empty) manager.</summary>
    string? SolutionPath { get; }

    /// <summary>The analyzer loader current at read time (shared-cache-backed, never disposed here).</summary>
    ShadowCopyAnalyzerAssemblyLoader? AnalyzerLoader { get; }

    /// <summary>
    /// Swaps the snapshot and the workspace in ONE critical section and returns the superseded
    /// workspace for the caller to dispose outside the lock.
    /// </summary>
    Workspace? SwapLifecycleAndSnapshot(
        LoadedSolution loaded,
        SymbolResolver resolver,
        MetadataSymbolResolver metadataResolver,
        Workspace? workspace);
}
