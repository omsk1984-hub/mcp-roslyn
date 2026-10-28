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

    /// <summary>
    /// Formats the "nothing to query" message for an empty workspace.
    ///
    /// <para>
    /// An empty workspace has two very different causes and they need different messages. Saying
    /// "No .sln file found" when a solution WAS found but every project failed to open sends the
    /// reader hunting for a missing file instead of at the load failure that actually happened
    /// (issue #399 was reported partly on the strength of this message).
    /// </para>
    /// </summary>
    public static string DescribeEmptyWorkspace(string? solutionPath, IReadOnlyList<SkippedProject> skipped)
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

    /// <summary>
    /// Compiles every project of the opened solution in the background and reports the result to
    /// <paramref name="ready"/>: the swapped-in snapshot is installed through
    /// <paramref name="install"/>, the warmup completion is signalled, and any failure is recorded
    /// so the readiness protocol can surface it.
    ///
    /// <para>
    /// Started by <see cref="SolutionManager.CreateAsync"/> and awaited only on first access — that
    /// non-blocking start is what keeps server startup fast and is pinned by
    /// <c>CreateAsync_ReturnsBeforeCompilationCompletes</c>.
    /// </para>
    /// </summary>
    public static async Task WarmupAsync(
        Opened opened,
        ReadyGate ready,
        Action<LoadedSolution> install,
        string? solutionPath,
        Action<FileChangeTracker>? attachTracker)
    {
        ready.ResetWarmupCompleted();
        try
        {
            await Console.Error.WriteLineAsync("[roslyn-codelens] Background compilation starting...").ConfigureAwait(false);
            var compilations = await opened.Loader.CompileAllParallelAsync(opened.Solution).ConfigureAwait(false);

            var newLoaded = CreateLoadedSolution(
                opened.Solution, compilations, opened.Skipped, opened.LoadDiagnostics);
            install(newLoaded);

            // The tracker watches the solution path the manager was told about, not the path the
            // opened solution reports — those differ for a directory-discovered solution.
            if (attachTracker != null && solutionPath != null)
                attachTracker(new FileChangeTracker(newLoaded, solutionPath));

            await Console.Error.WriteLineAsync(
                $"[roslyn-codelens] Background compilation complete. {compilations.Count} project(s) compiled.")
                .ConfigureAwait(false);
            ready.SetWarmup(null);
        }
        catch (Exception ex)
        {
            ready.RecordWarmupFailure(ex);
            await Console.Error.WriteLineAsync($"[roslyn-codelens] Background compilation failed: {ex}").ConfigureAwait(false);
        }
        finally
        {
            // Release synchronous waiters whatever the outcome; the recorded warmup failure carries
            // the error. Signalled before the task completes, which is fine: the state swap above
            // happens before both.
            ready.SignalWarmupCompleted();
        }
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
