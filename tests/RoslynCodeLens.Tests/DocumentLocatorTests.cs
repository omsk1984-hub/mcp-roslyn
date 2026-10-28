using RoslynCodeLens;
using RoslynCodeLens.Tests.Fixtures;
using Xunit;

namespace RoslynCodeLens.Tests;

[Collection("TestSolution")]
public class DocumentLocatorTests
{
    private readonly TestSolutionFixture _fixture;

    public DocumentLocatorTests(TestSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void TryFind_FoundFile_ReturnsTrue()
    {
        var filePath = Path.Combine(
            Path.GetDirectoryName(_fixture.SolutionPath)!,
            "TestLib", "Greeter.cs");

        var found = DocumentLocator.TryFind(
            _fixture.Loaded, filePath, out var project, out var document);

        Assert.True(found);
        Assert.NotNull(project);
        Assert.NotNull(document);
        Assert.Equal("TestLib", project!.Name);
        Assert.Equal(document!.FilePath, Path.GetFullPath(filePath), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryFind_NotFoundFile_ReturnsFalse()
    {
        var found = DocumentLocator.TryFind(
            _fixture.Loaded, @"D:\nonexistent\file.cs", out var project, out var document);

        Assert.False(found);
        Assert.Null(project);
        Assert.Null(document);
    }

    [Fact]
    public void TryFind_DifferentCase_ReturnsTrue()
    {
        var filePath = Path.Combine(
            Path.GetDirectoryName(_fixture.SolutionPath)!,
            "TestLib", "Greeter.cs");

        // Change case of the file name
        var upperCasePath = filePath.Replace("Greeter.cs", "GREETER.CS");

        var found = DocumentLocator.TryFind(
            _fixture.Loaded, upperCasePath, out var project, out var document);

        Assert.True(found);
        Assert.NotNull(project);
        Assert.NotNull(document);
    }

    [Fact]
    public void TryFind_FileInReferencedProject_ReturnsTrue()
    {
        // TestLib2 references TestLib; find a file in TestLib2
        var filePath = Path.Combine(
            Path.GetDirectoryName(_fixture.SolutionPath)!,
            "TestLib2", "CrossProjectGreeter.cs");

        var found = DocumentLocator.TryFind(
            _fixture.Loaded, filePath, out var project, out var document);

        Assert.True(found);
        Assert.NotNull(project);
        Assert.Equal("TestLib2", project!.Name);
    }

    [Fact]
    public void TryFindInProject_FoundFile_ReturnsTrue()
    {
        var filePath = Path.Combine(
            Path.GetDirectoryName(_fixture.SolutionPath)!,
            "TestLib", "Greeter.cs");

        // First find the project
        Assert.True(DocumentLocator.TryFind(
            _fixture.Loaded, filePath, out var project, out _));

        // Then use TryFindInProject
        var found = DocumentLocator.TryFindInProject(
            project, filePath, out var document);

        Assert.True(found);
        Assert.NotNull(document);
    }

    [Fact]
    public void TryFindInProject_WrongProject_ReturnsFalse()
    {
        var greeterPath = Path.Combine(
            Path.GetDirectoryName(_fixture.SolutionPath)!,
            "TestLib", "Greeter.cs");

        var crossProjectPath = Path.Combine(
            Path.GetDirectoryName(_fixture.SolutionPath)!,
            "TestLib2", "CrossProjectGreeter.cs");

        // Find TestLib project
        Assert.True(DocumentLocator.TryFind(
            _fixture.Loaded, greeterPath, out var testLibProject, out _));

        // Try to find CrossProjectGreeter.cs in TestLib — should fail
        var found = DocumentLocator.TryFindInProject(
            testLibProject, crossProjectPath, out var document);

        Assert.False(found);
        Assert.Null(document);
    }
}
