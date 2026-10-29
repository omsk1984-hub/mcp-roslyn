namespace RoslynCodeLens.Models;

public record CodeActionInfo(
    string Title,
    string Kind,
    string? EquivalenceKey = null,
    IReadOnlyList<CodeActionInfo>? NestedActions = null);
