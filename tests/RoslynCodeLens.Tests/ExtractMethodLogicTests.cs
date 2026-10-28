using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using Xunit;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Stage E: <c>extract_method</c> as a facade over Roslyn's own extract-method provider.
/// This machine reports the Russian-localized Roslyn resources (the action title comes back as
/// «Извлечь метод»), so every green test here also proves the action is located by its
/// locale-independent equivalence key rather than by its title.
/// </summary>
public class ExtractMethodLogicTests
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> FrameworkReferences =
        new(() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList());

    // Line map (1-based), referenced by the ranges below:
    //   8-9    statements inside ZeroArg()    — depends on nothing outside the range
    //   15-16  statements inside WithParams() — reads the a/b parameters
    //   4-5    the class declaration          — no method body, extraction impossible
    private const string Source =
        "using System;\n" +                                       // 1
        "namespace ExtractDemo;\n" +                             // 2
        "\n" +                                                   // 3
        "public class Foo\n" +                                   // 4
        "{\n" +                                                  // 5
        "    public void ZeroArg()\n" +                          // 6
        "    {\n" +                                              // 7
        "        Console.WriteLine(\"alpha\");\n" +              // 8
        "        Console.WriteLine(\"beta\");\n" +               // 9
        "        Console.WriteLine(\"gamma\");\n" +              // 10
        "    }\n" +                                              // 11
        "\n" +                                                   // 12
        "    public void WithParams(int a, int b)\n" +           // 13
        "    {\n" +                                              // 14
        "        Console.WriteLine(a);\n" +                      // 15
        "        Console.WriteLine(b);\n" +                      // 16
        "        Console.WriteLine(a + b);\n" +                  // 17
        "    }\n" +                                              // 18
        "}\n";                                                   // 19

    private const int ZeroArgStart = 8;
    private const int ZeroArgEnd = 9;
    private const int WithParamsStart = 15;
    private const int WithParamsEnd = 16;
    private const int ClassLevelStart = 4;
    private const int ClassLevelEnd = 5;

    private static (LoadedSolution Loaded, string Path) BuildSolution(string dir)
    {
        var path = Path.Combine(dir, "Foo.cs");
        File.WriteAllText(path, Source);

        // A solution file path so the trust gate (which keys off Solution.FilePath) stays
        // satisfiable in this in-memory workspace.
        var slnPath = Path.Combine(dir, "Extract.sln");
        File.WriteAllText(slnPath, "");

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
                projectId, VersionStamp.Create(), "ExtractProj", "ExtractProj", LanguageNames.CSharp,
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

    private static Task<ExtractMethodResult> RunAsync(
        LoadedSolution loaded, string path, int startLine, int endLine,
        string methodName, bool preview = true, bool force = false) =>
        ExtractMethodLogic.ExecuteAsync(
            loaded, path, startLine, endLine, methodName, preview, force,
            commitToMemory: null, CancellationToken.None);

    private static string[] ErrorsOf(string text) =>
        CSharpCompilation.Create("ExtractCheck",
                [CSharpSyntaxTree.ParseText(text)], FrameworkReferences.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id}: {d.GetMessage()}")
            .ToArray();

    // E.3.1 — extraction with no external dependencies: preview returns the edits, the new
    // method takes no parameters, and the disk file is untouched.
    [Fact]
    public async Task Preview_NoDependency_ExtractedMethodHasNoParameters()
    {
        var dir = Directory.CreateTempSubdirectory("extract-preview-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);

            var result = await RunAsync(loaded, path, ZeroArgStart, ZeroArgEnd, "EmitPrefix");

            Assert.True(result.Success);
            Assert.False(result.Applied);
            Assert.NotEmpty(result.Edits);
            Assert.Contains("EmitPrefix", result.NewMethodSignature, StringComparison.Ordinal);
            Assert.DoesNotContain("int ", result.NewMethodSignature, StringComparison.Ordinal);
            // Preview must not touch the file.
            Assert.Equal(Source, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // E.3.2 — extraction that reads the enclosing parameters: they become parameters of the
    // new method, reported in newMethodSignature.
    [Fact]
    public async Task Preview_UsesEnclosingParameters_ExtractedMethodTakesThem()
    {
        var dir = Directory.CreateTempSubdirectory("extract-params-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);

            var result = await RunAsync(loaded, path, WithParamsStart, WithParamsEnd, "EmitOperands");

            Assert.True(result.Success);
            Assert.Contains("EmitOperands", result.NewMethodSignature, StringComparison.Ordinal);
            Assert.Contains("int a", result.NewMethodSignature, StringComparison.Ordinal);
            Assert.Contains("int b", result.NewMethodSignature, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // E.3.3 — an invalid method name is rejected before any work happens.
    // Note: SyntaxFacts.IsValidIdentifier("class") is true — a keyword is a syntactically
    // valid identifier — so the rejected inputs are genuinely malformed names.
    [Theory]
    [InlineData("")]
    [InlineData("My Method")]
    [InlineData("1Method")]
    [InlineData("My.Method")]
    [InlineData("Int32&")]
    public async Task InvalidMethodName_ThrowsInvalidArgument(string methodName)
    {
        var dir = Directory.CreateTempSubdirectory("extract-badname-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);

            var ex = await Assert.ThrowsAsync<McpToolException>(() =>
                RunAsync(loaded, path, ZeroArgStart, ZeroArgEnd, methodName));

            Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
            Assert.Equal(Source, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // E.3.4 — a range outside any method body: the provider offers no extraction, and the tool
    // says so with the actions it did find (diagnosable instead of an empty response).
    [Fact]
    public async Task RangeOutsideMethod_ThrowsInvalidArgumentListingAvailableActions()
    {
        var dir = Directory.CreateTempSubdirectory("extract-nobody-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);

            var ex = await Assert.ThrowsAsync<McpToolException>(() =>
                RunAsync(loaded, path, ClassLevelStart, ClassLevelEnd, "Whatever"));

            Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
            Assert.Contains("Extract-method is not available", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // E.3.4b — a range that does not fit the document is rejected with the real line count.
    [Theory]
    [InlineData(0, 5)]
    [InlineData(9, 8)]
    [InlineData(1, 99)]
    public async Task InvalidRange_ThrowsInvalidArgument(int startLine, int endLine)
    {
        var dir = Directory.CreateTempSubdirectory("extract-badrange-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);

            var ex = await Assert.ThrowsAsync<McpToolException>(() =>
                RunAsync(loaded, path, startLine, endLine, "Whatever"));

            Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
            Assert.Contains("Invalid range", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A file that is not in the solution is reported as FileNotFound, not as a silent no-op.
    [Fact]
    public async Task UnknownFile_ThrowsFileNotFound()
    {
        var dir = Directory.CreateTempSubdirectory("extract-nofile-").FullName;
        try
        {
            var (loaded, _) = BuildSolution(dir);

            var ex = await Assert.ThrowsAsync<McpToolException>(() =>
                RunAsync(loaded, Path.Combine(dir, "Missing.cs"), 1, 2, "Whatever"));

            Assert.Equal(ToolErrorCode.FileNotFound, ex.Code);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Applying writes the extraction: the requested name is on disk, the extracted statements
    // are replaced by a call, and the result still compiles without new errors.
    [Fact]
    public async Task Apply_WritesExtractedMethod_WithRequestedName_AndCompiles()
    {
        var dir = Directory.CreateTempSubdirectory("extract-apply-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);

            var result = await RunAsync(loaded, path, WithParamsStart, WithParamsEnd,
                "EmitOperands", preview: false);

            Assert.True(result.Success);
            Assert.True(result.Applied);
            Assert.Empty(result.Conflicts);

            var onDisk = File.ReadAllText(path);
            Assert.Contains("EmitOperands", onDisk, StringComparison.Ordinal);
            // The extracted statements moved into the new method; the original body now holds
            // a call passing the captured parameters through.
            Assert.Matches(@"EmitOperands\(a, b\);", onDisk);
            Assert.Matches(@"void\s+EmitOperands\(int a, int b\)", onDisk);
            // Roslyn's generated name must not survive — the requested name won.
            Assert.DoesNotContain("NewMethod", onDisk, StringComparison.Ordinal);

            Assert.Empty(ErrorsOf(onDisk));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The extracted method is private and the untouched tail of the body is preserved.
    [Fact]
    public async Task Apply_ExtractedMethodIsPrivate_AndLeavesTheRestOfTheBody()
    {
        var dir = Directory.CreateTempSubdirectory("extract-access-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);

            var result = await RunAsync(loaded, path, ZeroArgStart, ZeroArgEnd,
                "EmitPrefix", preview: false);

            Assert.True(result.Success);
            var onDisk = File.ReadAllText(path);
            // Roslyn emits the extracted member as a private, parameterless instance method.
            Assert.Matches(@"private\s+(static\s+)?void\s+EmitPrefix\s*\(\s*\)", onDisk);
            // The statement outside the extracted range stays where it was.
            Assert.Contains("Console.WriteLine(\"gamma\");", onDisk, StringComparison.Ordinal);
            Assert.Empty(ErrorsOf(onDisk));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Refusing to write when the file changed on disk after the snapshot: the extraction is
    // computed from stale text, so apply must not clobber the newer content.
    [Fact]
    public async Task Apply_StaleFileOnDisk_RefusesToWrite()
    {
        var dir = Directory.CreateTempSubdirectory("extract-stale-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);

            // Simulate a concurrent edit after the snapshot was taken.
            File.WriteAllText(path, Source.Replace("\"beta\"", "\"BETA\""));

            var result = await RunAsync(loaded, path, ZeroArgStart, ZeroArgEnd,
                "EmitPrefix", preview: false);

            Assert.False(result.Success);
            Assert.False(result.Applied);
            Assert.Contains("changed on disk", result.Message, StringComparison.Ordinal);
            Assert.Contains("\"BETA\"", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
