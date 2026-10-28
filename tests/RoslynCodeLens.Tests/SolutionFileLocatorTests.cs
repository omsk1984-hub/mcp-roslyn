using RoslynCodeLens.Loading;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Pins the upward-search contract of solution discovery that used to live in
/// <c>SolutionLoader.FindSolutionFile</c>. Moved verbatim to <see cref="SolutionFileLocator"/>
/// as part of the stage-5.3 decomposition, so these tests guard the behaviour across the move.
/// </summary>
public sealed class SolutionFileLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rcl-locator-" + Guid.NewGuid().ToString("N"));

    public SolutionFileLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string MakeDir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Find_FindsSolutionInStartingDirectory()
    {
        var dir = MakeDir("a");
        var sln = Path.Combine(dir, "Only.sln");
        File.WriteAllText(sln, "");

        Assert.Equal(sln, SolutionFileLocator.Find(dir));
    }

    [Fact]
    public void Find_WalksUpToAncestorDirectory()
    {
        var deep = MakeDir("a", "b", "c");
        var sln = Path.Combine(_root, "Root.slnx");
        File.WriteAllText(sln, "");

        Assert.Equal(sln, SolutionFileLocator.Find(deep));
    }

    [Fact]
    public void Find_ChoosesShortestFullPathWithinADirectory()
    {
        // Both files live in the same directory; the shorter full name must win,
        // regardless of enumeration order or extension.
        var dir = MakeDir("a");
        var shortName = Path.Combine(dir, "S.slnx");
        var longName = Path.Combine(dir, "MuchLongerName.sln");
        File.WriteAllText(longName, "");
        File.WriteAllText(shortName, "");

        Assert.Equal(shortName, SolutionFileLocator.Find(dir));
    }

    [Fact]
    public void Find_HandlesBothSlnAndSlnxExtensions()
    {
        var dir = MakeDir("a");
        var slnx = Path.Combine(dir, "S.slnx");
        File.WriteAllText(slnx, "");

        Assert.Equal(slnx, SolutionFileLocator.Find(dir));
    }

    [Fact]
    public void Find_ReturnsNullWhenNoSolutionAnywhereUpTheTree()
    {
        var deep = MakeDir("no-solution", "deeper");

        // Only assert for the isolated temp tree: if the machine's real ancestors happen to
        // hold a solution, the search legitimately returns it, so skip that comparison.
        var found = SolutionFileLocator.Find(deep);
        if (found is not null)
        {
            Assert.False(found.StartsWith(_root, StringComparison.OrdinalIgnoreCase),
                "A solution was reported inside the isolated temp tree, which contains none.");
        }
    }
}
