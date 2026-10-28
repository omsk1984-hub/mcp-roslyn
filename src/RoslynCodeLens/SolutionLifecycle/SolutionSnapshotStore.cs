using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Metadata;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.SolutionLifecycle;

/// <summary>
/// Owns the swappable analysis snapshot of a loaded solution — the <see cref="LoadedSolution"/>
/// together with the two resolvers built from it — behind a single lock. Extracted from
/// <see cref="SolutionManager"/> (stage 5.2) because this triple and its lock are what invariant
/// #282 is about, and every writer in the manager (warmup, incremental rebuild, commit, full
/// reload) needs to swap it under the same lock.
///
/// <para>
/// <b>Invariant #282 — the reason this type exists.</b> The three parts MUST be replaced together
/// and read together. #282 was a silent-empty bug: a caller that paired a <c>Solution</c> with a
/// resolver built from a DIFFERENT snapshot got zero results from <c>SymbolFinder</c> instead of
/// an error. <see cref="Snapshot"/> is therefore the ONLY way to read the triple, and it always
/// reads all three under one lock — a reader can never observe a half-applied swap.
/// </para>
///
/// <para>
/// <b>Deadlock note.</b> This store's lock must NOT be held while awaiting the readiness protocol
/// or a rebuild: warmup and the rebuild paths take the same lock to publish their result, so
/// holding it across an <c>await</c> would self-deadlock. Callers run the protocol first, then
/// call <see cref="Snapshot"/> — that ordering is what keeps <c>EnsureReady</c> outside the lock.
/// </para>
///
/// <para>
/// This type deliberately does no rebuilding and no I/O: it is state plus the lock. Choosing WHEN
/// to rebuild belongs to <see cref="StaleRebuildCoordinator"/>, and deciding whether the manager
/// is ready at all belongs to the readiness gate.
/// </para>
/// </summary>
public sealed class SolutionSnapshotStore
{
    private readonly Lock _lock = new();
    private LoadedSolution _loaded;
    private SymbolResolver _resolver;
    private MetadataSymbolResolver _metadataResolver;

    public SolutionSnapshotStore(
        LoadedSolution loaded, SymbolResolver resolver, MetadataSymbolResolver metadataResolver)
    {
        _loaded = loaded;
        _resolver = resolver;
        _metadataResolver = metadataResolver;
    }

    /// <summary>
    /// The current snapshot, read under one lock so the three parts are guaranteed to come from
    /// the same swap (invariant #282).
    /// </summary>
    public SolutionAnalysisContext Snapshot()
    {
        lock (_lock)
            return new SolutionAnalysisContext(_loaded, _resolver, _metadataResolver);
    }

    /// <summary>
    /// Replaces all three parts atomically. Every writer in the manager funnels through here so
    /// none can install a solution from one swap and a resolver from another.
    /// </summary>
    public void Swap(LoadedSolution loaded, SymbolResolver resolver, MetadataSymbolResolver metadataResolver)
    {
        lock (_lock)
        {
            _loaded = loaded;
            _resolver = resolver;
            _metadataResolver = metadataResolver;
        }
    }

    /// <summary>
    /// Builds the pre-warmup snapshot published by <see cref="SolutionManager.CreateAsync"/>: the
    /// opened solution is queryable immediately, but no project has been compiled yet, so
    /// <see cref="LoadedSolution.IsEmpty"/> is true until warmup fills the compilations in.
    /// </summary>
    public static LoadedSolution CreateLoadedSolution(
        Solution solution,
        IReadOnlyList<SkippedProject>? skipped = null,
        IReadOnlyList<string>? loadDiagnostics = null)
    {
        return new LoadedSolution
        {
            Solution = solution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation>(),
            SkippedProjects = skipped ?? Array.Empty<SkippedProject>(),
            LoadDiagnostics = loadDiagnostics ?? Array.Empty<string>()
        };
    }

    /// <summary>
    /// Builds a <see cref="LoadedSolution"/> from an already-compiled set of projects — the shape
    /// every rebuild produces. Kept here beside <see cref="CreateLoadedSolution"/> so the two
    /// ways a snapshot comes into existence are defined in one place.
    /// </summary>
    public static LoadedSolution CreateLoadedSolution(
        Solution solution,
        ConcurrentDictionary<ProjectId, Compilation> compilations,
        IReadOnlyList<SkippedProject> skipped,
        IReadOnlyList<string> loadDiagnostics)
    {
        return new LoadedSolution
        {
            Solution = solution,
            Compilations = compilations,
            SkippedProjects = skipped,
            LoadDiagnostics = loadDiagnostics
        };
    }
}
