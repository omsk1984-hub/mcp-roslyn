using Microsoft.CodeAnalysis;

namespace RoslynCodeLens;

/// <summary>
/// Отвечает на вопрос «где этот символ находится»: файл, строка, проект, а также
/// является ли файл сгенерированным и из какого markup-файла он получен.
///
/// Выделено из <see cref="SymbolResolver"/> (этап 5.1 плана SOLID-аудита): раньше эти
/// семь членов жили в god-объекте вместе с поиском по имени и иерархиями типов, хотя
/// не используют ни один из тех индексов — только собственные словари путей.
///
/// Индекс неизменяем: всё строится в конструкторе по одному снимку решения.
/// </summary>
public sealed class LocationIndex
{
    private readonly Dictionary<string, string> _fileToProjectName;
    private readonly Dictionary<ProjectId, string> _projectIdToName;
    private readonly HashSet<string> _generatedFilePaths;
    private readonly Dictionary<string, string> _generatedToMarkup;

    public LocationIndex(LoadedSolution loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);

        (_fileToProjectName, _projectIdToName) = BuildProjectLookups(loaded);
        _generatedFilePaths = BuildGeneratedFileIndex(loaded);
        _generatedToMarkup = Analysis.MarkupDocumentMap.BuildGeneratedToMarkup(loaded);
    }

    private static (Dictionary<string, string>, Dictionary<ProjectId, string>)
        BuildProjectLookups(LoadedSolution loaded)
    {
        var fileToProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var idToName = new Dictionary<ProjectId, string>();

        foreach (var project in loaded.Solution.Projects)
        {
            idToName[project.Id] = project.Name;
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null)
                    fileToProject[doc.FilePath] = project.Name;
            }

            // Source-generated documents are not in project.Documents, but their symbols are
            // real and get returned by search_symbols/find_references. Indexing only the
            // on-disk documents left every generated symbol reporting an empty project name
            // — which is most symbols in a Blazor project, where components live in
            // generator output. The compilation is the only place both kinds appear together.
            if (loaded.Compilations.TryGetValue(project.Id, out var compilation))
            {
                foreach (var tree in compilation.SyntaxTrees)
                {
                    if (!string.IsNullOrEmpty(tree.FilePath))
                        fileToProject[tree.FilePath] = project.Name;
                }
            }
        }

        return (fileToProject, idToName);
    }

    private static HashSet<string> BuildGeneratedFileIndex(LoadedSolution loaded)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var compilation in loaded.Compilations.Values)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (IsGeneratedPath(tree.FilePath))
                    paths.Add(tree.FilePath);
            }
        }
        return paths;
    }

    /// <summary>
    /// The first source location of a symbol, or null when the symbol has none
    /// (metadata symbols, error symbols).
    /// </summary>
    public static Location? GetLocation(ISymbol symbol)
    {
        return symbol.Locations.FirstOrDefault(l => l.IsInSource);
    }

    /// <summary>
    /// File path and 1-based line of a symbol's first source location;
    /// <c>("", 0)</c> when the symbol has no source location.
    /// </summary>
    public (string File, int Line) GetFileAndLine(ISymbol symbol)
    {
        var location = GetLocation(symbol);
        if (location == null) return ("", 0);

        var lineSpan = location.GetLineSpan();
        return (lineSpan.Path, lineSpan.StartLinePosition.Line + 1);
    }

    /// <summary>
    /// Name of the project declaring the symbol, or empty when the symbol is not in source.
    /// </summary>
    public string GetProjectName(ISymbol symbol)
    {
        var location = GetLocation(symbol);
        if (location?.SourceTree == null) return "";

        return _fileToProjectName.TryGetValue(location.SourceTree.FilePath, out var name)
            ? name
            : "";
    }

    public string GetProjectName(ProjectId projectId)
    {
        return _projectIdToName.TryGetValue(projectId, out var name) ? name : "";
    }

    /// <summary>
    /// The markup file (.razor/.cshtml) a generated document was produced from, or null when the
    /// file is not generator output from markup. Lets results that necessarily point into obj/
    /// name the file the user can actually edit.
    /// </summary>
    public string? GetMarkupSource(string? filePath) =>
        filePath is not null && _generatedToMarkup.TryGetValue(filePath, out var markup) ? markup : null;

    public bool IsGenerated(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return true;
        if (_generatedFilePaths.Contains(filePath))
            return true;
        return IsGeneratedPath(filePath);
    }

    private static bool IsGeneratedPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return true;
        return path.Contains("/obj/", StringComparison.Ordinal)
            || path.Contains("\\obj\\", StringComparison.Ordinal);
    }
}
