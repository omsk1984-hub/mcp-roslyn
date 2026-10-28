using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens.Analyzers;

namespace RoslynCodeLens.Loading;

/// <summary>
/// Turns a workspace-loaded <see cref="Project"/> into a self-contained
/// <see cref="ProjectInfo"/> that survives disposal of the workspace it came from.
/// </summary>
/// <remarks>
/// Used by per-project loading: each worker opens projects in its own
/// <c>MSBuildWorkspace</c> and disposes it, so everything the rest of the load needs
/// (document text, options, references) must be materialised in memory first.
/// ProjectReferences are intentionally stripped here — their ids are local to the
/// worker workspace and are re-wired from the on-disk graph after re-stitch.
/// </remarks>
internal static class DetachedProjectFactory
{
    /// <summary>
    /// Detaches a workspace-loaded <paramref name="project"/> into a self-contained
    /// <see cref="ProjectInfo"/> with document text materialised in memory and all
    /// ProjectReferences stripped, so it survives disposal of its source workspace
    /// and can be re-stitched into a fresh workspace. The project keeps its own
    /// (globally-unique) id and document ids.
    /// </summary>
    public static async Task<ProjectInfo> ToDetachedInfoAsync(Project project, ShadowCopyAnalyzerAssemblyLoader? loader)
    {
        var documents = new List<DocumentInfo>();
        foreach (var d in project.Documents)
            documents.Add(await ToDocumentInfoAsync(d, d.SourceCodeKind).ConfigureAwait(false));

        var additional = new List<DocumentInfo>();
        foreach (var d in project.AdditionalDocuments)
            additional.Add(await ToDocumentInfoAsync(d, SourceCodeKind.Regular).ConfigureAwait(false));

        var analyzerConfig = new List<DocumentInfo>();
        foreach (var d in project.AnalyzerConfigDocuments)
            analyzerConfig.Add(await ToDocumentInfoAsync(d, SourceCodeKind.Regular).ConfigureAwait(false));

        return ProjectInfo.Create(
                project.Id,
                VersionStamp.Create(),
                project.Name,
                project.AssemblyName,
                project.Language,
                filePath: project.FilePath,
                outputFilePath: project.OutputFilePath,
                compilationOptions: project.CompilationOptions,
                parseOptions: project.ParseOptions,
                documents: documents,
                projectReferences: Array.Empty<ProjectReference>(),
                metadataReferences: project.MetadataReferences,
                analyzerReferences: loader is null
                    ? project.AnalyzerReferences
                    : AnalyzerReferenceRemapper.Remap(project.AnalyzerReferences, loader),
                additionalDocuments: additional)
            .WithAnalyzerConfigDocuments(analyzerConfig)
            .WithDefaultNamespace(project.DefaultNamespace);
    }

    private static async Task<DocumentInfo> ToDocumentInfoAsync(TextDocument document, SourceCodeKind kind)
    {
        var text = await document.GetTextAsync().ConfigureAwait(false);
        var loader = TextLoader.From(
            TextAndVersion.Create(text, VersionStamp.Create(), document.FilePath ?? document.Name));

        return DocumentInfo.Create(
            document.Id,
            document.Name,
            folders: document.Folders,
            sourceCodeKind: kind,
            loader: loader,
            filePath: document.FilePath);
    }
}
