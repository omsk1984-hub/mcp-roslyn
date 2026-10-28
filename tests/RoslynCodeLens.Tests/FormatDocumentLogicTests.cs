using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;
using Xunit;

namespace RoslynCodeLens.Tests;

public class FormatDocumentLogicTests
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> FrameworkReferences =
        new(() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList());

    // C.3.1 — unformatted source: preview returns edits, disk file unchanged.
    [Fact]
    public async Task Unformatted_Preview_ReturnsEdits_DiskUntouched()
    {
        var dir = Directory.CreateTempSubdirectory("format-preview-").FullName;
        try
        {
            var path = Path.Combine(dir, "Foo.cs");
            const string source = "namespace FormatDemo;\npublic class Foo{public   int X;}\n";
            await File.WriteAllTextAsync(path, source);
            var (loaded, _) = RenameTestWorkspace.Create((path, source));

            var result = await FormatDocumentLogic.ExecuteAsync(
                loaded, path, lineRange: null, preview: true, force: false,
                commitToMemory: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.False(result.Applied);
            Assert.NotEmpty(result.Edits);
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // C.3.2 — preview: false writes the formatted file to disk and the result compiles.
    [Fact]
    public async Task Unformatted_Apply_WritesFormattedFile_ThatCompiles()
    {
        var dir = Directory.CreateTempSubdirectory("format-apply-").FullName;
        try
        {
            var path = Path.Combine(dir, "Foo.cs");
            const string source = "namespace FormatDemo;\npublic class Foo{public   int X;}\n";
            await File.WriteAllTextAsync(path, source);
            var (loaded, _) = RenameTestWorkspace.Create((path, source));

            var result = await FormatDocumentLogic.ExecuteAsync(
                loaded, path, lineRange: null, preview: false, force: false,
                commitToMemory: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(result.Applied);
            var onDisk = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("public   int", onDisk, StringComparison.Ordinal);
            // The formatted result must still compile (no new errors).
            Assert.Empty(CompileErrors(onDisk));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // C.3.3 — already-formatted file: applied=false, edits empty, mtime unchanged.
    [Fact]
    public async Task AlreadyFormatted_NoOp_MtimeUnchanged()
    {
        var dir = Directory.CreateTempSubdirectory("format-noop-").FullName;
        try
        {
            var path = Path.Combine(dir, "Foo.cs");
            const string source = "namespace FormatDemo;\n\npublic class Foo\n{\n    public int X;\n}\n";
            await File.WriteAllTextAsync(path, source);
            var (loaded, _) = RenameTestWorkspace.Create((path, source));

            var mtimeBefore = File.GetLastWriteTimeUtc(path);
            var result = await FormatDocumentLogic.ExecuteAsync(
                loaded, path, lineRange: null, preview: false, force: false,
                commitToMemory: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.False(result.Applied);
            Assert.Empty(result.Edits);
            Assert.Equal("Already formatted.", result.Message);
            Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // C.3.4 — fragment formatting only touches the requested line range.
    [Fact]
    public async Task Fragment_OnlyTouchesRequestedRange()
    {
        var dir = Directory.CreateTempSubdirectory("format-fragment-").FullName;
        try
        {
            var path = Path.Combine(dir, "Foo.cs");
            const string source =
                "namespace FormatDemo;\n" +
                "\n" +
                "public class Foo\n" +
                "{\n" +
                "    public   int   X;\n" +
                "    public int Y;\n" +
                "}\n";
            await File.WriteAllTextAsync(path, source);
            var (loaded, _) = RenameTestWorkspace.Create((path, source));

            var result = await FormatDocumentLogic.ExecuteAsync(
                loaded, path, lineRange: [5, 5], preview: false, force: false,
                commitToMemory: null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(result.Applied);
            var onDisk = await File.ReadAllTextAsync(path);
            // The requested line was normalized (extra spaces collapsed).
            Assert.DoesNotContain("public   int", onDisk, StringComparison.Ordinal);
            // Prefix (lines 1-4) and suffix (lines 6-7) are byte-identical.
            Assert.StartsWith("namespace FormatDemo;\n\npublic class Foo\n{\n", onDisk, StringComparison.Ordinal);
            Assert.EndsWith("    public int Y;\n}\n", onDisk, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // C.3.5 — file outside the solution → structured FileNotFound error.
    [Fact]
    public async Task FileOutsideSolution_ThrowsFileNotFound()
    {
        var dir = Directory.CreateTempSubdirectory("format-outside-").FullName;
        try
        {
            var path = Path.Combine(dir, "Foo.cs");
            const string source = "namespace FormatDemo;\npublic class Foo{public   int X;}\n";
            await File.WriteAllTextAsync(path, source);
            var (loaded, _) = RenameTestWorkspace.Create((path, source));

            var outsidePath = Path.Combine(dir, "NotInSolution.cs");
            await File.WriteAllTextAsync(outsidePath, source);

            var ex = await Assert.ThrowsAsync<McpToolException>(() =>
                FormatDocumentLogic.ExecuteAsync(
                    loaded, outsidePath, lineRange: null, preview: true, force: false,
                    commitToMemory: null, CancellationToken.None));

            Assert.Equal(ToolErrorCode.FileNotFound, ex.Code);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static IReadOnlyList<Diagnostic> CompileErrors(string source)
    {
        var compilation = CSharpCompilation.Create(
            "FormatCheck",
            [CSharpSyntaxTree.ParseText(source)],
            FrameworkReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
    }
}
