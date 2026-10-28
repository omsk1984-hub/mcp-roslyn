using Microsoft.CodeAnalysis;

namespace RoslynCodeLens;

/// <summary>
/// Единая точка поиска документа по абсолютному пути: убирает четыре копии
/// цикла project/document и даёт один и тот же ответ на вопрос
/// «в каком проекте и документе этот файл».
/// </summary>
/// <remarks>
/// Документ может быть сгенерированным (Razor) — см.
/// <c>GetFileOverviewLogic.FindGeneratedDocumentForMarkupAsync</c>.
/// Генерация НЕ входит в <see cref="TryFind"/> (остаётся в <c>get_file_overview</c>).
/// </remarks>
public static class DocumentLocator
{
    /// <summary>
    /// Ищет документ по абсолютному пути во всех проектах загруженного решения.
    /// Нормализует путь через <see cref="Path.GetFullPath"/> и сравнивает
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// </summary>
    /// <param name="loaded">Загруженное решение.</param>
    /// <param name="filePath">Путь к файлу (нормализуется автоматически).</param>
    /// <param name="project">Найденный проект (null, если не найден).</param>
    /// <param name="document">Найденный документ (null, если не найден).</param>
    /// <returns>true, если документ найден; false — иначе (не исключение).</returns>
    public static bool TryFind(
        LoadedSolution loaded, string filePath,
        out Project project, out Document document)
    {
        var normalizedPath = Path.GetFullPath(filePath);

        foreach (var proj in loaded.Solution.Projects)
        {
            foreach (var doc in proj.Documents)
            {
                if (doc.FilePath != null &&
                    doc.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    project = proj;
                    document = doc;
                    return true;
                }
            }
        }

        project = null!;
        document = null!;
        return false;
    }

    /// <summary>
    /// Ищет документ в конкретном проекте (когда проект уже известен).
    /// </summary>
    /// <param name="project">Проект для поиска.</param>
    /// <param name="filePath">Путь к файлу (нормализуется автоматически).</param>
    /// <param name="document">Найденный документ (null, если не найден).</param>
    /// <returns>true, если документ найден; false — иначе.</returns>
    public static bool TryFindInProject(
        Project project, string filePath,
        out Document document)
    {
        var normalizedPath = Path.GetFullPath(filePath);

        foreach (var doc in project.Documents)
        {
            if (doc.FilePath != null &&
                doc.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                document = doc;
                return true;
            }
        }

        document = null!;
        return false;
    }
}
