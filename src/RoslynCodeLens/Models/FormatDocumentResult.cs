namespace RoslynCodeLens.Models;

/// <summary>
/// Result of a <c>format_document</c> operation. Mirrors the shape of
/// <see cref="CodeActionResult"/> but adds <see cref="Applied"/> and
/// <see cref="Conflicts"/> so the agent can distinguish "preview returned
/// edits" from "bytes hit disk" and see any new compiler errors the
/// formatting would introduce.
/// </summary>
public sealed record FormatDocumentResult(
    bool Success,
    string FilePath,
    bool Applied,
    IReadOnlyList<TextEdit> Edits,
    IReadOnlyList<RenameConflict> Conflicts,
    string Message);
