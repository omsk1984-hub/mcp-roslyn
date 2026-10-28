namespace RoslynCodeLens.Loading;

/// <summary>
/// Locates a solution file by walking <em>upward</em> from a starting directory.
/// </summary>
/// <remarks>
/// Discovery is deliberately one-directional: the search never descends into
/// subdirectories. When a directory holds more than one <c>*.sln</c>/<c>*.slnx</c>,
/// the candidate with the shortest full path wins and the rest are ignored
/// silently — callers that need a specific file must pass it explicitly.
/// Split out of <c>SolutionLoader</c> (stage 5.3) because it is file-system
/// discovery, independent of opening and compiling projects.
/// </remarks>
internal static class SolutionFileLocator
{
    /// <summary>Returns the nearest solution file at or above <paramref name="startDirectory"/>, or null.</summary>
    public static string? Find(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir != null)
        {
            FileInfo? shortest = null;
            foreach (var f in dir.GetFiles("*.sln"))
            {
                if (shortest == null || f.FullName.Length < shortest.FullName.Length)
                    shortest = f;
            }
            foreach (var f in dir.GetFiles("*.slnx"))
            {
                if (shortest == null || f.FullName.Length < shortest.FullName.Length)
                    shortest = f;
            }
            if (shortest != null)
                return shortest.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
