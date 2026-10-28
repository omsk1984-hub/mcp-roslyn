using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynCodeLens.Analyzers;
using RoslynCodeLens.Metadata;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.SolutionLifecycle;

/// <summary>
/// The composition root of a single solution's analysis stack: opens the solution, builds the
/// snapshot/resolver set, starts the background warmup, and performs the full reload used by
/// <c>rebuild_solution</c>. Extracted from <see cref="SolutionManager"/> (stage 5.2) so the façade
/// holds the contract while the wiring lives here.
///
/// <para>
/// <b>Why a separate type.</b> Opening a solution and recompiling every project is a different
/// reason to change from serving the public contract: it moves with <see cref="SolutionLoader"/>,
/// the analyzer shadow-copy strategy and MSBuild, all of which the façade should not know about.
/// </para>
///
/// <para>
/// <b>Non-blocking creation.</b> <see cref="OpenAsync"/> starts the warmup in the BACKGROUND and
/// returns immediately; the wait happens on first access through
/// <see cref="ReadyGate"/>. Moving that wait in here would regress server startup and is pinned
/// against by <c>CreateAsync_ReturnsBeforeCompilationCompletes</c>.
/// </para>
/// </summary>
public static class SolutionLifecycleFactory
{
    /// <summary>
    /// The process-lifetime shared shadow-copy cache (never disposed): the cross-solution dedup
    /// layer. Individual solutions release their entries through their own analyzer loader on
    /// dispose (refcount--), so entries unload when the last owner goes.
    /// </summary>
    private static readonly SharedAnalyzerAssemblyCache SharedCache = new();

    /// <summary>
    /// Opens <paramref name="solutionPath"/> and returns everything <see cref="SolutionManager"/>
    /// needs to construct itself. On failure the loader is disposed and the error is described into
    /// <see cref="OpenResult.LoadFailure"/> rather than thrown, so the manager can exist in a
    /// queryable-but-failed state (every accessor then throws the original message).
    /// </summary>
    public static async Task<OpenResult> OpenAsync(string solutionPath, ProjectFilter? filter)
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
            return new OpenResult(null, null, solutionPath, ex, SolutionLoadFailure.Describe(solutionPath, ex));
        }

        var emptyLoaded = CreateLoadedSolution(
            open.Solution, skipped: open.Skipped, loadDiagnostics: open.LoadDiagnostics);

        return new OpenResult(
            new Opened(emptyLoaded, solutionLoader, open.Solution, open.Workspace, analyzerLoader,
                open.Skipped, open.LoadDiagnostics),
            analyzerLoader, solutionPath, null, null);
    }

    /// <summary>
    /// Builds the pre-warmup snapshot: the opened solution is queryable immediately, but no project
    /// has been compiled yet, so <see cref="LoadedSolution.IsEmpty"/> is true until warmup fills the
    /// compilations in.
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
    /// every rebuild produces.
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

    /// <summary>
    /// Re-opens the solution from disk and recompiles every project — the explicit reload. Returns
    /// the new state for the manager to install plus the superseded workspace/loader to dispose
    /// AFTER the swap. Throws (unlike <see cref="OpenAsync"/>) because an explicit reload that
    /// cannot open the solution is a real error the caller must see.
    /// </summary>
    public static async Task<ReloadResult> ReloadAsync(string solutionPath)
    {
        var solutionLoader = new SolutionLoader();
        var analyzerLoader = new ShadowCopyAnalyzerAssemblyLoader(SharedCache);
        SolutionLoader.SolutionOpen open;
        try
        {
            open = await solutionLoader.OpenAsync(solutionPath, null, analyzerLoader).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            analyzerLoader.Dispose();
            throw new InvalidOperationException(SolutionLoadFailure.Describe(solutionPath, ex), ex);
        }

        var solution = open.Solution;
        var compilations = await solutionLoader.CompileAllParallelAsync(solution).ConfigureAwait(false);
        var newLoaded = CreateLoadedSolution(solution, compilations, open.Skipped, open.LoadDiagnostics);

        return new ReloadResult(newLoaded, open.Workspace, analyzerLoader);
    }

    /// <summary>The successfully opened pieces, before any warmup has run.</summary>
    public sealed record Opened(
        LoadedSolution Loaded,
        SolutionLoader Loader,
        Solution Solution,
        Workspace Workspace,
        ShadowCopyAnalyzerAssemblyLoader AnalyzerLoader,
        IReadOnlyList<SkippedProject> Skipped,
        IReadOnlyList<string> LoadDiagnostics);

    /// <summary>
    /// Either <see cref="Opened"/> (success) or the load failure to record. Exactly one is non-null.
    /// </summary>
    public sealed record OpenResult(
        Opened? Opened,
        ShadowCopyAnalyzerAssemblyLoader? AnalyzerLoader,
        string SolutionPath,
        Exception? LoadFailure,
        string? LoadFailureMessage);

    /// <summary>The recompiled state of an explicit reload, plus what the caller must dispose.</summary>
    public sealed record ReloadResult(
        LoadedSolution Loaded,
        Workspace Workspace,
        ShadowCopyAnalyzerAssemblyLoader AnalyzerLoader);
}
