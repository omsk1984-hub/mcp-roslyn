using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Security;
using RoslynCodeLens.Tools;
using Xunit;

namespace RoslynCodeLens.Tests;

public class ApplyFixLogicTests
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> FrameworkReferences =
        new(() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList());

    // A file with a CS0246 error: List<int> is used without the required using.
    private const string Source =
        "namespace FixDemo;\n" +
        "\n" +
        "public class Foo\n" +
        "{\n" +
        "    public List<int> X;\n" +
        "}\n";

    private static (LoadedSolution Loaded, string Path) BuildSolution(string dir)
    {
        var path = Path.Combine(dir, "Foo.cs");
        File.WriteAllText(path, Source);

        // A solution file path so the trust gate (which keys off Solution.FilePath)
        // can be satisfied in this in-memory workspace.
        var slnPath = Path.Combine(dir, "Fix.sln");
        File.WriteAllText(slnPath, "");

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
                projectId, VersionStamp.Create(), "FixProj", "FixProj", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithMetadataReferences(FrameworkReferences.Value);
        var solutionInfo = Microsoft.CodeAnalysis.SolutionInfo.Create(
            SolutionId.CreateNewId(), VersionStamp.Create(), slnPath, [projectInfo]);
        var solution = workspace.AddSolution(solutionInfo);
        solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "Foo.cs",
            SourceText.From(Source), filePath: path);

        var compilations = new ConcurrentDictionary<ProjectId, Compilation>();
        compilations[projectId] = solution.GetProject(projectId)!
            .GetCompilationAsync().GetAwaiter().GetResult()!;

        var loaded = new LoadedSolution { Solution = solution, Compilations = compilations };
        return (loaded, path);
    }

    private static (TrustStore Trust, AnalyzerAllowlist Allowlist) NewSecurity(string slnPath, bool trusted)
    {
        var trust = new TrustStore(Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}.json"));
        if (trusted)
            trust.AddSessionTrust(slnPath);
        var allowlist = new AnalyzerAllowlist("all", AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);
        return (trust, allowlist);
    }

    private static int FindCs0246Line(Compilation compilation)
    {
        var diag = compilation.GetDiagnostics()
            .First(d => d.Id == "CS0246" && d.Location.IsInSource);
        return diag.Location.GetLineSpan().StartLinePosition.Line + 1;
    }

    // D.3.1 — CS0246 "using" fix: preview returns edits, disk file unchanged.
    [Fact]
    public async Task Cs0246_Preview_ReturnsEdits_DiskUntouched()
    {
        var dir = Directory.CreateTempSubdirectory("applyfix-preview-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);
            var (trust, allowlist) = NewSecurity(Path.Combine(dir, "Fix.sln"), trusted: true);
            var line = FindCs0246Line(loaded.Compilations.Values.First());

            var result = await ApplyFixLogic.ExecuteAsync(
                loaded, path, line, column: null,
                "CS0246", fixIndex: 0, preview: true, force: false,
                trust, allowlist, commitToMemory: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.False(result.Applied);
            Assert.NotEmpty(result.Edits);
            Assert.Equal(Source, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // D.3.1 — CS0246 "using" fix applied: the using is added and the error disappears.
    [Fact]
    public async Task Cs0246_Apply_UsingFix_FixesError()
    {
        var dir = Directory.CreateTempSubdirectory("applyfix-apply-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);
            var (trust, allowlist) = NewSecurity(Path.Combine(dir, "Fix.sln"), trusted: true);
            var compilation = loaded.Compilations.Values.First();
            var line = FindCs0246Line(compilation);

            // Locate the "using" fix specifically (its title contains the C# keyword
            // "using" in every locale) and apply that index, per the plan.
            var project = loaded.Solution.Projects.First();
            var doc = project.Documents.First(d => d.FilePath != null &&
                d.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            var diag = compilation.GetDiagnostics()
                .First(d => d.Id == "CS0246" && d.Location.IsInSource);
            var actions = await CodeActionRunner.GetCodeFixActionsAsync(
                project, doc, compilation, diag, CancellationToken.None);
            var usingIndex = -1;
            for (var i = 0; i < actions.Count; i++)
                if (actions[i].Title.Contains("using", StringComparison.OrdinalIgnoreCase))
                { usingIndex = i; break; }
            Assert.True(usingIndex >= 0, $"no using fix among {actions.Count} CS0246 fixes");

            var result = await ApplyFixLogic.ExecuteAsync(
                loaded, path, line, column: null,
                "CS0246", fixIndex: usingIndex, preview: false, force: false,
                trust, allowlist, commitToMemory: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(result.Applied);
            var onDisk = File.ReadAllText(path);
            Assert.Contains("using System.Collections.Generic;", onDisk, StringComparison.Ordinal);
            // The error is gone: recompile the on-disk text and confirm no CS0246.
            var recomp = CSharpCompilation.Create("FixCheck",
                [CSharpSyntaxTree.ParseText(onDisk)], FrameworkReferences.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.DoesNotContain(recomp.GetDiagnostics(), d => d.Id == "CS0246");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // D.3.2 — fixIndex out of range → InvalidArgument with the available count.
    [Fact]
    public async Task FixIndexOutOfRange_ThrowsInvalidArgument()
    {
        var dir = Directory.CreateTempSubdirectory("applyfix-range-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);
            var (trust, allowlist) = NewSecurity(Path.Combine(dir, "Fix.sln"), trusted: true);
            var line = FindCs0246Line(loaded.Compilations.Values.First());

            var ex = await Assert.ThrowsAsync<McpToolException>(() =>
                ApplyFixLogic.ExecuteAsync(
                    loaded, path, line, column: null,
                    "CS0246", fixIndex: 99, preview: true, force: false,
                    trust, allowlist, commitToMemory: null, CancellationToken.None));

            Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // D.3.3 — no diagnostic of that id on the line → success:false with an explanatory message.
    [Fact]
    public async Task NoDiagnosticOnLine_ReturnsSuccessFalse()
    {
        var dir = Directory.CreateTempSubdirectory("applyfix-nodiag-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);
            var (trust, allowlist) = NewSecurity(Path.Combine(dir, "Fix.sln"), trusted: true);

            var result = await ApplyFixLogic.ExecuteAsync(
                loaded, path, line: 1, column: null,
                "CS0246", fixIndex: 0, preview: true, force: false,
                trust, allowlist, commitToMemory: null, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("No CS0246 diagnostic", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // D.3.4 — untrusted solution → SolutionNotTrusted.
    [Fact]
    public async Task UntrustedSolution_ThrowsSolutionNotTrusted()
    {
        var dir = Directory.CreateTempSubdirectory("applyfix-untrusted-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);
            var (trust, allowlist) = NewSecurity(Path.Combine(dir, "Fix.sln"), trusted: false);
            var line = FindCs0246Line(loaded.Compilations.Values.First());

            var ex = await Assert.ThrowsAsync<McpToolException>(() =>
                ApplyFixLogic.ExecuteAsync(
                    loaded, path, line, column: null,
                    "CS0246", fixIndex: 0, preview: true, force: false,
                    trust, allowlist, commitToMemory: null, CancellationToken.None));

            Assert.Equal(ToolErrorCode.SolutionNotTrusted, ex.Code);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
