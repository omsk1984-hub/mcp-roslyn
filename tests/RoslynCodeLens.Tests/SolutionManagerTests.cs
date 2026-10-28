using Microsoft.CodeAnalysis.Text;

namespace RoslynCodeLens.Tests;

public class SolutionManagerTests : IAsyncLifetime
{
    private string _solutionPath = null!;

    public Task InitializeAsync()
    {
        _solutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateAsync_LoadsSolutionAndResolver()
    {
        var manager = await SolutionManager.CreateAsync(_solutionPath);

        Assert.NotNull(manager.GetLoadedSolution());
        Assert.False(manager.GetLoadedSolution().IsEmpty);
        Assert.NotNull(manager.GetResolver());
        manager.Dispose();
    }

    [Fact]
    public async Task GetResolver_ReturnsCachedInstance_WhenNotStale()
    {
        var manager = await SolutionManager.CreateAsync(_solutionPath);
        var resolver1 = manager.GetResolver();
        var resolver2 = manager.GetResolver();

        Assert.Same(resolver1, resolver2);
        manager.Dispose();
    }

    [Fact]
    public void EnsureLoaded_ThrowsForEmptySolution()
    {
        var manager = SolutionManager.CreateEmpty();
        Assert.Throws<InvalidOperationException>(() => manager.EnsureLoaded());
        manager.Dispose();
    }

    // Issue #399: an empty workspace has two causes that need different messages. Reporting
    // "No .sln file found" for a solution that WAS found but whose projects all failed to open
    // sends the reader hunting for a missing file instead of at the actual load failure.
    [Fact]
    public void DescribeEmptyWorkspace_SaysNoSolution_WhenNonePassedOrDiscovered()
    {
        var message = SolutionManager.DescribeEmptyWorkspace(null, Array.Empty<SkippedProject>());

        Assert.Contains("No solution found", message, StringComparison.Ordinal);
        Assert.Contains(".slnx", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeEmptyWorkspace_NamesTheSolution_WhenItLoadedWithNoProjects()
    {
        var message = SolutionManager.DescribeEmptyWorkspace(
            @"C:/code/MyApp.slnx", Array.Empty<SkippedProject>());

        Assert.Contains("MyApp.slnx", message, StringComparison.Ordinal);
        Assert.DoesNotContain("No solution found", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeEmptyWorkspace_ReportsSkipReasons_WhenEveryProjectWasSkipped()
    {
        var skipped = new[]
        {
            new SkippedProject(@"C:/code/A.csproj", "A", "Legacy", "non-SDK project"),
            new SkippedProject(@"C:/code/B.csproj", "B", "Failed", "design-time build failed"),
        };

        var message = SolutionManager.DescribeEmptyWorkspace(@"C:/code/MyApp.slnx", skipped);

        Assert.Contains("MyApp.slnx", message, StringComparison.Ordinal);
        Assert.Contains("all 2 project(s) were skipped", message, StringComparison.Ordinal);
        Assert.Contains("A: non-SDK project", message, StringComparison.Ordinal);
        Assert.Contains("B: design-time build failed", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeEmptyWorkspace_TruncatesLongSkipLists()
    {
        var skipped = Enumerable.Range(0, 8)
            .Select(i => new SkippedProject($"C:/code/P{i}.csproj", $"P{i}", "Failed", "boom"))
            .ToArray();

        var message = SolutionManager.DescribeEmptyWorkspace(@"C:/code/MyApp.slnx", skipped);

        Assert.Contains("all 8 project(s) were skipped", message, StringComparison.Ordinal);
        Assert.Contains("(and 3 more)", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_ReturnsBeforeCompilationCompletes()
    {
        var manager = await SolutionManager.CreateAsync(_solutionPath);

        // After warmup, resolver should have data
        await manager.WaitForWarmupAsync();
        var resolver = manager.GetResolver();

        Assert.True(resolver.AllTypes.Count > 0);
        manager.Dispose();
    }

    [Fact]
    public async Task CommitDocumentTexts_ImmediateQueriesSeeNewText_WithoutAnyWatcherEvent()
    {
        // Finding 5: after rename_symbol writes files, the manager's in-memory snapshot must
        // reflect the new text immediately — no disk write happens here at all, so the file
        // watcher can never deliver this change; only CommitDocumentTextsAsync can.
        var manager = await SolutionManager.CreateAsync(_solutionPath);
        try
        {
            await manager.WaitForWarmupAsync();
            var before = manager.GetAnalysisContext();
            Assert.Empty(before.Resolver.FindSymbols("CommittedProbe"));

            var doc = before.Loaded.Solution.Projects
                .First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal))
                .Documents.First(d => d.FilePath != null);
            var text = await doc.GetTextAsync();
            var newText = SourceText.From(
                text + "\n\npublic class CommittedProbe { }\n", text.Encoding);

            await manager.CommitDocumentTextsAsync([(doc.Id, newText)]);

            var after = manager.GetAnalysisContext();
            // Resolver rebuilt from the committed snapshot sees the new type immediately.
            Assert.NotEmpty(after.Resolver.FindSymbols("CommittedProbe"));
            // The solution text and the cached compilation were both refreshed.
            var committedText = await after.Loaded.Solution.GetDocument(doc.Id)!.GetTextAsync();
            Assert.Contains("CommittedProbe", committedText.ToString(), StringComparison.Ordinal);
            Assert.Contains(
                after.Loaded.Compilations[doc.Project.Id].SyntaxTrees,
                t => t.ToString().Contains("CommittedProbe", StringComparison.Ordinal));
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task CommitDocumentTexts_UnknownDocumentId_IsANoOp()
    {
        // After a full reload mints fresh DocumentIds, a commit computed against the old
        // snapshot must be skipped gracefully (disk already holds the text; the reload read it).
        var manager = await SolutionManager.CreateAsync(_solutionPath);
        try
        {
            await manager.WaitForWarmupAsync();
            var before = manager.GetAnalysisContext();

            var foreignId = Microsoft.CodeAnalysis.DocumentId.CreateNewId(
                Microsoft.CodeAnalysis.ProjectId.CreateNewId());
            await manager.CommitDocumentTextsAsync(
                [(foreignId, SourceText.From("public class Ghost { }"))]);

            var after = manager.GetAnalysisContext();
            Assert.Same(before.Loaded, after.Loaded);   // nothing applied — no snapshot swap
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task GetResolver_AwaitsWarmupIfNotReady()
    {
        var manager = await SolutionManager.CreateAsync(_solutionPath);

        // GetResolver should block until warmup is done and return valid resolver
        var resolver = manager.GetResolver();
        Assert.True(resolver.AllTypes.Count > 0);
        manager.Dispose();
    }

    // ---------------------------------------------------------------------
    // Stage 3 / item 1: CHARACTERISTIC tests for the snapshot-access protocol.
    //
    // These pin the CURRENT behaviour of the five accessors so the upcoming
    // de-duplication (one EnsureReady() shared by all of them) cannot silently
    // change semantics. Every test here MUST be green on the pre-refactor code;
    // a red one means the copies have already diverged and is a finding in its
    // own right — never "fix" the test to match new behaviour.
    //
    // Scenario coverage:
    //   (a) HasLoadFailure  -> InvalidOperationException carrying the original message
    //   (v) stale projects  -> rebuild happens before the value is handed out
    //   (b) failed warmup   -> NOT covered here, see decision/ note: there is no
    //                           deterministic seam to make CompileAllParallelAsync
    //                           throw against a real fixture, and no production
    //                           test hook was added on purpose.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Scenario (a): once the solution failed to load, EVERY snapshot accessor raises
    /// <see cref="InvalidOperationException"/> carrying the original failure message
    /// (the accessors differ only in the value they would otherwise return).
    /// </summary>
    [Fact]
    public async Task Accessors_OnLoadFailure_ThrowWithOriginalMessage()
    {
        using var manager = await CreateLoadFailureManagerAsync();
        Assert.True(manager.HasLoadFailure);
        var expected = manager.LoadFailureMessage;
        Assert.False(string.IsNullOrWhiteSpace(expected));

        var thrown = new List<Exception>
        {
            Assert.Throws<InvalidOperationException>(() => manager.GetLoadedSolution()),
            Assert.Throws<InvalidOperationException>(() => manager.GetResolver()),
            Assert.Throws<InvalidOperationException>(() => manager.GetMetadataResolver()),
            Assert.Throws<InvalidOperationException>(() => manager.GetAnalysisContext()),
            Assert.Throws<InvalidOperationException>(() => manager.EnsureLoaded()),
        };

        Assert.All(thrown, ex => Assert.Equal(expected, ex.Message));
        // The original loader failure is preserved as the inner exception on all of them.
        Assert.All(thrown, ex => Assert.NotNull(ex.InnerException));
    }

    /// <summary>
    /// Scenario (a), second half — the documented EXCEPTION to the protocol:
    /// <see cref="SolutionManager.GetIlDisassembler"/> neither throws on a failed load nor
    /// waits for warmup. Its body is `=> _ilAdapter`, a field initialised in both
    /// constructors, so it is reachable even when nothing loaded.
    ///
    /// This is pinned as DEFECT D3-new, NOT as desirable behaviour: an accessor that hands
    /// out state without the readiness protocol lets a caller observe a not-ready manager
    /// as if it were ready. Stage 3 item 5 decides whether to fold it into the protocol;
    /// until then the test documents what the code actually does.
    /// </summary>
    [Fact]
    public async Task GetIlDisassembler_OnLoadFailure_DoesNotThrow_DefectD3New()
    {
        using var manager = await CreateLoadFailureManagerAsync();
        Assert.True(manager.HasLoadFailure);

        // DEFECT D3-new: no ThrowIfLoadFailed(), no warmup wait — contrast with the other
        // accessors above, which all throw here.
        var adapter = manager.GetIlDisassembler();
        Assert.NotNull(adapter);
    }

    /// <summary>
    /// Produces a manager in the REAL load-failure state, through the production path only.
    ///
    /// <para>
    /// <c>SolutionManager.CreateAsync</c> marks a manager as failed when
    /// <c>SolutionLoader.OpenAsync</c> throws; it then catches and records the exception.
    /// A merely nonexistent or corrupt <c>.sln/.slnx</c> is NOT enough — classification
    /// swallows parse errors, the per-project fallback records skips, and the result is an
    /// empty (not failed) solution. An invalid path is what actually throws: the very first
    /// statement of <c>OpenAsync</c> calls <c>ProjectClassifier.EnumerateProjects</c>, whose
    /// <c>Path.GetFullPath</c> rejects embedded NULs — outside any try/catch.
    /// </para>
    /// </summary>
    private static Task<SolutionManager> CreateLoadFailureManagerAsync()
    {
        var invalid = Path.Combine(Path.GetTempPath(), "invalid\0path\0solution.slnx");
        return SolutionManager.CreateAsync(invalid);
    }

    /// <summary>
    /// Scenario (v): pending stale projects are rebuilt BEFORE the value is returned, for
    /// every snapshot accessor. Verified by observing a newly added type through each
    /// accessor's own answer path (resolver types, metadata resolver, loaded solution).
    /// </summary>
    [Fact]
    public async Task Accessors_WithStaleProjects_RebuildBeforeReturning()
    {
        // Edit an EXISTING fixture document: a new file would be a structural change
        // requiring a full MSBuild reload, not the incremental rebuild this test pins.
        var consumerPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "TestSolution", "TestLib2", "GreeterConsumer.cs"));
        var original = await File.ReadAllTextAsync(consumerPath);

        SolutionManager? manager = null;
        try
        {
            manager = await SolutionManager.CreateAsync(_solutionPath);
            await manager.WaitForWarmupAsync();

            // Nothing named StaleProbe exists yet, as seen through each accessor.
            Assert.Empty(manager.GetResolver().FindSymbols("StaleProbe"));
            Assert.Null(manager.GetMetadataResolver().Resolve("StaleProbe"));
            Assert.Empty(manager.GetAnalysisContext().Resolver.FindSymbols("StaleProbe"));

            await File.WriteAllTextAsync(
                consumerPath,
                original + "\n\npublic class StaleProbe { }\n");
            manager.SimulateFileChangeForTest(consumerPath);

            // Each accessor rebuilds the stale project before answering, so the type appears
            // on the very next call — no explicit reload, no second accessor needed.
            Assert.NotEmpty(manager.GetResolver().FindSymbols("StaleProbe"));
            Assert.NotNull(manager.GetMetadataResolver().Resolve("StaleProbe"));
            Assert.NotEmpty(manager.GetAnalysisContext().Resolver.FindSymbols("StaleProbe"));
        }
        finally
        {
            await File.WriteAllTextAsync(consumerPath, original);
            manager?.Dispose();
        }
    }

    /// <summary>
    /// Scenario (v) for <see cref="SolutionManager.GetLoadedSolution"/>: the returned snapshot
    /// already contains the stale project's new text, i.e. the rebuild ran before the value
    /// was handed out rather than after.
    /// </summary>
    [Fact]
    public async Task GetLoadedSolution_WithStaleProjects_RebuildsBeforeReturning()
    {
        var consumerPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "TestSolution", "TestLib2", "GreeterConsumer.cs"));
        var original = await File.ReadAllTextAsync(consumerPath);

        SolutionManager? manager = null;
        try
        {
            manager = await SolutionManager.CreateAsync(_solutionPath);
            await manager.WaitForWarmupAsync();

            const string marker = "LoadedSolutionStaleProbe";
            Assert.All(
                manager.GetLoadedSolution().Solution.Projects.SelectMany(p => p.Documents),
                d => Assert.DoesNotContain(marker, d.Name, StringComparison.Ordinal));

            await File.WriteAllTextAsync(
                consumerPath,
                original + $"\n\npublic class {marker} {{ }}\n");
            manager.SimulateFileChangeForTest(consumerPath);

            var loaded = manager.GetLoadedSolution();
            var doc = loaded.Solution.Projects
                .SelectMany(p => p.Documents)
                .First(d => string.Equals(d.FilePath, consumerPath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(marker, (await doc.GetTextAsync()).ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await File.WriteAllTextAsync(consumerPath, original);
            manager?.Dispose();
        }
    }
}
