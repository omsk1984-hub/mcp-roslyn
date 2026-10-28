namespace RoslynCodeLens.Models;

/// <summary>
/// Result of an <c>extract_method</c> operation. Mirrors <see cref="ApplyFixResult"/>:
/// <see cref="Applied"/> distinguishes "preview returned edits" from "bytes hit disk",
/// and <see cref="Conflicts"/> surfaces new compiler errors the extraction would introduce.
/// <see cref="NewMethodSignature"/> is the signature Roslyn computed for the extracted
/// method (parameters it inferred from the captured locals), reported after the requested
/// name has been applied.
/// </summary>
public sealed record ExtractMethodResult(
    bool Success,
    string MethodName,
    string NewMethodSignature,
    bool Applied,
    IReadOnlyList<TextEdit> Edits,
    IReadOnlyList<RenameConflict> Conflicts,
    string Message);
