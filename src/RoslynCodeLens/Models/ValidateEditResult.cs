namespace RoslynCodeLens.Models;

/// <summary>
/// Read-only post-edit gate: one call answers "did my edit break the solution?" —
/// compiler/analyzer diagnostics per edited file, symbol liveness, and (optionally)
/// a delta against a previous snapshot. Replaces the diagnostics → file overview →
/// references chain with a single summary.
/// </summary>
/// <param name="Passed">
/// True when the checked files have 0 errors, there are 0 new errors against the
/// baseline (when one is supplied), and every requested symbol resolved. Explicitly
/// false when no file could be checked and at least one was skipped (outside the
/// loaded solution) — an empty error list must not read as "verified".
/// </param>
/// <param name="Files">Per-file report for every <c>filePaths</c> entry that was found in the solution.</param>
/// <param name="NewErrors">Errors present now but absent from the baseline. Null when no baseline was supplied.</param>
/// <param name="ResolvedErrors">Errors present in the baseline but gone now. Null when no baseline was supplied.</param>
/// <param name="Symbols">Per-symbol liveness report for every entry in <c>symbols</c>.</param>
/// <param name="Summary">Aggregate counters and the skipped/degraded flags.</param>
/// <param name="Message">One-line verdict for an agent that reads only the string.</param>
public sealed record ValidateEditResult(
    bool Passed,
    IReadOnlyList<FileValidationReport> Files,
    IReadOnlyList<DiagnosticInfo>? NewErrors,
    IReadOnlyList<DiagnosticInfo>? ResolvedErrors,
    IReadOnlyList<SymbolValidationReport> Symbols,
    ValidateEditSummary Summary,
    string Message);

/// <summary>
/// Diagnostics and declared types for one edited file.
/// </summary>
/// <param name="FilePath">Normalized absolute path of the file.</param>
/// <param name="Project">Project the file belongs to.</param>
/// <param name="TypesDefined">Type names declared in this file.</param>
/// <param name="Errors">Compiler diagnostics at severity Error.</param>
/// <param name="Warnings">Compiler diagnostics at severity Warning.</param>
/// <param name="AnalyzerFindings">Analyzer diagnostics (source prefixed with "analyzer:").</param>
public sealed record FileValidationReport(
    string FilePath,
    string Project,
    IReadOnlyList<string> TypesDefined,
    IReadOnlyList<DiagnosticInfo> Errors,
    IReadOnlyList<DiagnosticInfo> Warnings,
    IReadOnlyList<DiagnosticInfo> AnalyzerFindings);

/// <summary>
/// Liveness of one requested symbol.
/// </summary>
/// <param name="Symbol">The symbol name as requested.</param>
/// <param name="Resolved">Whether the symbol was found in source or metadata.</param>
/// <param name="ReferenceCount">Total reference count (0 when unresolved).</param>
/// <param name="ByKind">Reference count grouped by reference kind. Empty when unresolved.</param>
/// <param name="DanglingCandidates">
/// When unresolved: files where the symbol's simple name still appears as an identifier
/// (a soft hint that something references it textually). Empty when resolved.
/// </param>
public sealed record SymbolValidationReport(
    string Symbol,
    bool Resolved,
    int ReferenceCount,
    IReadOnlyDictionary<string, int> ByKind,
    IReadOnlyList<string> DanglingCandidates);

/// <summary>
/// Aggregate counters for a validate_edit run.
/// </summary>
/// <param name="ErrorCount">Total compiler errors across checked files.</param>
/// <param name="WarningCount">Total compiler warnings across checked files.</param>
/// <param name="FilesChecked">Number of requested files found in the solution.</param>
/// <param name="SymbolsChecked">Number of symbols checked.</param>
/// <param name="SkippedFiles">Requested file paths not found in the loaded solution.</param>
/// <param name="LoadDegraded">True when the solution loaded with dropped references.</param>
public sealed record ValidateEditSummary(
    int ErrorCount,
    int WarningCount,
    int FilesChecked,
    int SymbolsChecked,
    IReadOnlyList<string> SkippedFiles,
    bool LoadDegraded);

/// <summary>
/// A snapshot of a previous validate_edit response's errors, used to compute the
/// new/resolved delta. Built by the tool from a baseline JSON file.
/// </summary>
/// <param name="Errors">The error diagnostics from the previous run.</param>
public sealed record ValidateEditBaseline(IReadOnlyList<DiagnosticInfo> Errors);
