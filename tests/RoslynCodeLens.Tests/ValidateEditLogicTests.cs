using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Stage A (plan 27): validate_edit — the read-only post-edit gate. Tests build small
/// in-memory solutions (deterministic, no fixture dependency) so each required case is
/// pinned to exact diagnostics rather than to whatever the shared fixture happens to hold.
/// </summary>
public class ValidateEditLogicTests
{
    private static string MakePath(string name) =>
        Path.Combine(Path.GetTempPath(), "validate-edit-tests", name);

    private static (RoslynCodeLens.Security.TrustStore Trust, RoslynCodeLens.Security.AnalyzerAllowlist Allowlist) NewSecurity()
    {
        var trust = new RoslynCodeLens.Security.TrustStore(Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}.json"));
        var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist(
            "all", RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);
        return (trust, allowlist);
    }

    /// <summary>
    /// The full trusted-platform-assemblies closure of the test host, built once and shared —
    /// a bare corelib reference leaves System.Object undefined (CS0518) and every type
    /// unresolved, so the compilation must see the whole TPA set to behave like a loaded solution.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<MetadataReference>> FrameworkReferences =
        new(() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList());

    /// <summary>
    /// One in-memory project holding the given (path, source) files, with a real compilation
    /// so compiler diagnostics and symbol resolution behave like a loaded solution.
    /// </summary>
    private static (LoadedSolution Loaded, SymbolResolver Resolver) BuildSolution(params (string Path, string Source)[] files)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var compilations = new ConcurrentDictionary<ProjectId, Compilation>();

        var projectId = ProjectId.CreateNewId();
        solution = solution.AddProject(ProjectInfo.Create(
            projectId, VersionStamp.Create(), "TestProj", "TestProj", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithMetadataReferences(FrameworkReferences.Value));

        foreach (var (path, source) in files)
        {
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId), Path.GetFileName(path),
                SourceText.From(source), filePath: path);
        }

        compilations[projectId] = solution.GetProject(projectId)!
            .GetCompilationAsync().GetAwaiter().GetResult()!;

        var loaded = new LoadedSolution { Solution = solution, Compilations = compilations };
        return (loaded, new SymbolResolver(loaded));
    }

    // 1. Clean file → passed: true, errorCount: 0.
    [Fact]
    public async Task CleanFile_Passes()
    {
        var (loaded, resolver) = BuildSolution((MakePath("Clean.cs"), "class A { }"));
        var (trust, allowlist) = NewSecurity();

        var result = await ValidateEditLogic.ExecuteAsync(
            loaded, resolver, [MakePath("Clean.cs")], null, "error", false,
            trust, allowlist, null, CancellationToken.None);

        Assert.True(result.Passed, $"passed; msg={result.Message}");
        Assert.Equal(0, result.Summary.ErrorCount);
        Assert.Single(result.Files);
        Assert.Empty(result.Files[0].Errors);
    }

    // 2. Syntax error → passed: false, error in files[0].errors, Id starts with CS.
    [Fact]
    public async Task SyntaxError_Fails_WithCsError()
    {
        var path = MakePath("Broken.cs");
        var (loaded, resolver) = BuildSolution((path, "class A { int x = ; }"));
        var (trust, allowlist) = NewSecurity();

        var result = await ValidateEditLogic.ExecuteAsync(
            loaded, resolver, [path], null, "error", false,
            trust, allowlist, null, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Single(result.Files);
        Assert.NotEmpty(result.Files[0].Errors);
        Assert.All(result.Files[0].Errors, e => Assert.StartsWith("CS", e.Id, StringComparison.Ordinal));
        Assert.Equal(1, result.Summary.ErrorCount);
    }

    // 3. Existing symbol → resolved: true, referenceCount > 0, byKind non-empty.
    //    The Assert.NotEmpty guard (case 7) is folded in: if the in-memory consumer ever stops
    //    referencing Greeter, referenceCount collapses to 0 and this test fails.
    [Fact]
    public async Task ExistingSymbol_Resolves_WithReferences()
    {
        var (loaded, resolver) = BuildSolution(
            (MakePath("Greeter.cs"), "namespace T; public class Greeter { public string Greet(string n) => n; }"),
            (MakePath("Consumer.cs"), "namespace T; public class Consumer { public string Use() { var g = new Greeter(); return g.Greet(\"x\"); } }"));
        var (trust, allowlist) = NewSecurity();

        var result = await ValidateEditLogic.ExecuteAsync(
            loaded, resolver, [MakePath("Greeter.cs")], ["Greeter"], "error", false,
            trust, allowlist, null, CancellationToken.None);

        var report = Assert.Single(result.Symbols);
        Assert.True(report.Resolved);
        Assert.Contains(result.Symbols.Select(s => s.ReferenceCount), c => c > 0);
        Assert.True(report.ReferenceCount > 0, "expected at least one reference to Greeter");
        Assert.NotEmpty(report.ByKind);
        Assert.True(result.Passed);
    }

    // 4. Non-existent symbol + textual mention in a comment → resolved: false,
    //    danglingCandidates contains the file.
    [Fact]
    public async Task MissingSymbol_WithTextualMention_ReportsDangling()
    {
        var path = MakePath("Mentions.cs");
        var (loaded, resolver) = BuildSolution((path, "// TODO: wire up Ghost later\nclass A { }"));
        var (trust, allowlist) = NewSecurity();

        var result = await ValidateEditLogic.ExecuteAsync(
            loaded, resolver, [path], ["Ghost"], "error", false,
            trust, allowlist, null, CancellationToken.None);

        var report = Assert.Single(result.Symbols);
        Assert.False(report.Resolved);
        Assert.Equal(0, report.ReferenceCount);
        Assert.Contains(path, report.DanglingCandidates, StringComparer.OrdinalIgnoreCase);
        Assert.False(result.Passed);
    }

    // 5. Path outside the solution → skippedFiles contains the path, and passed is NOT true
    //    just because errors is empty (filesChecked == 0 && skippedFiles.Count > 0 → false).
    [Fact]
    public async Task PathOutsideSolution_Skipped_AndNotPassed()
    {
        var (loaded, resolver) = BuildSolution((MakePath("Inside.cs"), "class A { }"));
        var (trust, allowlist) = NewSecurity();
        var outside = MakePath("outside-solution.cs");

        var result = await ValidateEditLogic.ExecuteAsync(
            loaded, resolver, [outside], null, "error", false,
            trust, allowlist, null, CancellationToken.None);

        Assert.Empty(result.Files);
        Assert.Contains(outside, result.Summary.SkippedFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, result.Summary.FilesChecked);
        Assert.False(result.Passed);
        Assert.Contains("outside the loaded solution", result.Message, StringComparison.Ordinal);
    }

    // 6a. Baseline without error, current with error → newErrors contains exactly one.
    [Fact]
    public async Task BaselineDelta_NewErrorReported()
    {
        var path = MakePath("Broken.cs");
        var (loaded, resolver) = BuildSolution((path, "class A { int x = ; }"));
        var (trust, allowlist) = NewSecurity();
        var baseline = new ValidateEditBaseline([]);

        var result = await ValidateEditLogic.ExecuteAsync(
            loaded, resolver, [path], null, "error", false,
            trust, allowlist, baseline, CancellationToken.None);

        Assert.NotNull(result.NewErrors);
        Assert.Single(result.NewErrors!);
        Assert.Empty(result.ResolvedErrors!);
        Assert.False(result.Passed);
    }

    // 6b. Baseline with an error that is gone now → resolvedErrors contains it.
    [Fact]
    public async Task BaselineDelta_ResolvedErrorReported()
    {
        var path = MakePath("Clean.cs");
        var (loaded, resolver) = BuildSolution((path, "class A { }"));
        var (trust, allowlist) = NewSecurity();
        var baseline = new ValidateEditBaseline(
        [
            new DiagnosticInfo("CS1002", "Error", "Semicolon expected", path, 1, "TestProj", "compiler", 10)
        ]);

        var result = await ValidateEditLogic.ExecuteAsync(
            loaded, resolver, [path], null, "error", false,
            trust, allowlist, baseline, CancellationToken.None);

        Assert.NotNull(result.ResolvedErrors);
        Assert.Single(result.ResolvedErrors!);
        Assert.Empty(result.NewErrors!);
        // No current errors and no new errors → passed even though the baseline had one.
        Assert.True(result.Passed);
    }

    // 6c. Stage 0 / item 3 (fix(stage0.3)): the `passed` computation dereferences
    //     `newErrors` under `baseline is null || newErrors.Count == 0` (CS8602). The
    //     compiler cannot prove the delta path populated it. This pins the invariant the
    //     fix must preserve: with a baseline supplied, NewErrors is always non-null and
    //     empty here, and Passed follows the error count.
    [Fact]
    public async Task BaselineDelta_SuppliedBaselineAlwaysPopulatesNewErrors()
    {
        var path = MakePath("CleanWithBaseline.cs");
        var (loaded, resolver) = BuildSolution((path, "class A { }"));
        var (trust, allowlist) = NewSecurity();
        var baseline = new ValidateEditBaseline([]);

        var result = await ValidateEditLogic.ExecuteAsync(
            loaded, resolver, [path], null, "error", false,
            trust, allowlist, baseline, CancellationToken.None);

        // Non-null is what makes the `newErrors.Count` dereference safe.
        Assert.NotNull(result.NewErrors);
        Assert.Empty(result.NewErrors!);
        Assert.True(result.Passed);
    }

    // 6d. Stage 0 / item 3: the null-baseline path must never touch NewErrors at all —
    //     no baseline means no delta, so NewErrors stays null while Passed is computed
    //     from the error list alone. Pins both branches of the `passed` expression.
    [Fact]
    public async Task NoBaseline_LeavesNewErrorsNull_AndPassesOnCleanFile()
    {
        var path = MakePath("CleanNoBaseline.cs");
        var (loaded, resolver) = BuildSolution((path, "class A { }"));
        var (trust, allowlist) = NewSecurity();

        var result = await ValidateEditLogic.ExecuteAsync(
            loaded, resolver, [path], null, "error", false,
            trust, allowlist, baseline: null, CancellationToken.None);

        Assert.Null(result.NewErrors);
        Assert.Null(result.ResolvedErrors);
        Assert.True(result.Passed);
    }
}
