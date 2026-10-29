using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens;
using RoslynCodeLens.Tools;
using Xunit;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Verifies that <c>get_code_actions</c> surfaces the locale-independent
/// <see cref="RoslynCodeLens.Models.CodeActionInfo.EquivalenceKey"/> alongside the
/// (localized) title. This machine reports Russian-localized Roslyn resources, so a green
/// test here also proves the extract-method action is identifiable by its stable key
/// ("Extract_method") rather than by its localized title («Извлечь метод»).
/// </summary>
public class GetCodeActionsEquivalenceKeyTests
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> FrameworkReferences =
        new(() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList());

    // Line map (1-based): the two statements on lines 8-9 sit inside ZeroArg()'s block body,
    // which is the range Roslyn's extract-method refactoring provider acts on.
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
        "}\n";                                                   // 12

    private const int RangeStart = 8;
    private const int RangeEnd = 9;

    private static (LoadedSolution Loaded, string Path) BuildSolution(string dir)
    {
        var path = Path.Combine(dir, "Foo.cs");
        File.WriteAllText(path, Source);

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

    // The extract-method action must be present for a statement range inside a method body,
    // and it must carry the locale-independent equivalence key.
    [Fact]
    public async Task ExecuteAsync_ExtractMethodAction_CarriesEquivalenceKey()
    {
        var dir = Directory.CreateTempSubdirectory("codeactions-key-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);

            var result = await GetCodeActionsLogic.ExecuteAsync(
                loaded, path, line: RangeStart, column: 9,
                endLine: RangeEnd, endColumn: 35, CancellationToken.None);

            Assert.NotEmpty(result);

            var extract = result.FirstOrDefault(a =>
                string.Equals(a.EquivalenceKey, "Extract_method", StringComparison.Ordinal));
            Assert.NotNull(extract);
            // The title is localized (ru-RU here) — the key is the stable identity.
            Assert.False(string.IsNullOrEmpty(extract!.Title));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Every action that Roslyn tags with an equivalence key must surface it; actions without
    // one report null (the field is optional, not a fabricated empty string).
    [Fact]
    public async Task ExecuteAsync_EquivalenceKey_MatchesRawActions()
    {
        var dir = Directory.CreateTempSubdirectory("codeactions-key-raw-").FullName;
        try
        {
            var (loaded, path) = BuildSolution(dir);

            var project = loaded.Solution.Projects.Single();
            var document = project.Documents.Single();
            var compilation = loaded.Compilations[project.Id];

            var raw = await CodeActionRunner.GetRawActionsAsync(
                project, document, compilation,
                RangeStart, 9, RangeEnd, 35, CancellationToken.None);
            var info = await GetCodeActionsLogic.ExecuteAsync(
                loaded, path, line: RangeStart, column: 9,
                endLine: RangeEnd, endColumn: 35, CancellationToken.None);

            Assert.NotEmpty(raw);
            // The flattened top-level set must line up 1:1 with the raw actions by key.
            Assert.Equal(
                raw.Select(a => a.EquivalenceKey).OrderBy(k => k, StringComparer.Ordinal),
                info.Select(a => a.EquivalenceKey).OrderBy(k => k, StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
