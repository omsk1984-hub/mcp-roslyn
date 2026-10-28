namespace RoslynCodeLens.Models;

/// <summary>
/// Result of an <c>apply_fix</c> operation. Mirrors the shape of
/// <see cref="FormatDocumentResult"/>: <see cref="Applied"/> distinguishes
/// "preview returned edits" from "bytes hit disk", and <see cref="Conflicts"/>
/// surfaces any new compiler errors the fix would introduce.
/// </summary>
public sealed record ApplyFixResult(
    bool Success,
    string DiagnosticId,
    string ActionTitle,
    bool Applied,
    IReadOnlyList<TextEdit> Edits,
    IReadOnlyList<RenameConflict> Conflicts,
    string Message);
