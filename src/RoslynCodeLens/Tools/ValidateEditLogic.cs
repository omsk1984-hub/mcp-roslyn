using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

/// <summary>
/// Read-only post-edit gate: one call answers "did my edit break the solution?" —
/// compiler/analyzer diagnostics per edited file, symbol liveness, and (optionally)
/// a delta against a previous snapshot. Replaces the diagnostics → file overview →
/// references chain. Diagnostics are collected through <see cref="GetDiagnosticsLogic"/>
/// (never a second, divergent collection path) so this tool cannot become a "second
/// truth" about diagnostics.
/// </summary>
public static class ValidateEditLogic
{
    /// <summary>Default cap on dangling-candidate files reported per unresolved symbol.</summary>
    public const int DanglingLimit = 50;

    public static async Task<ValidateEditResult> ExecuteAsync(
        LoadedSolution loaded,
        SymbolResolver resolver,
        IReadOnlyList<string> filePaths,
        IReadOnlyList<string>? symbols,
        string? severity,
        bool includeAnalyzers,
        Security.TrustStore trustStore,
        Security.AnalyzerAllowlist allowlist,
        ValidateEditBaseline? baseline,
        CancellationToken ct)
    {
        var metadata = new MetadataSymbolResolver(loaded, resolver);

        // 1. Resolve each requested path; the ones not in the solution are skipped (not an error).
        var found = new List<(string Normalized, Project Project, Document Document)>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in filePaths)
        {
            ct.ThrowIfCancellationRequested();
            var normalized = Path.GetFullPath(raw);
            if (!seen.Add(normalized))
                continue;
            if (DocumentLocator.TryFind(loaded, normalized, out var project, out var document))
                found.Add((normalized, project, document));
            else
                skipped.Add(normalized);
        }

        // 2. Per-file diagnostics + declared types, scoped server-side to the file (stage B filter).
        var files = new List<FileValidationReport>();
        foreach (var (normalized, project, document) in found)
        {
            ct.ThrowIfCancellationRequested();
            var diags = await GetDiagnosticsLogic.ExecuteAsync(
                loaded, resolver, project: null, severity,
                includeAnalyzers, trustStore, allowlist,
                filePath: normalized, ct: ct).ConfigureAwait(false);

            var errors = diags.Where(d => d.Source == "compiler" && d.Severity == "Error").ToList();
            var warnings = diags.Where(d => d.Source == "compiler" && d.Severity == "Warning").ToList();
            var analyzer = diags.Where(d => d.Source.StartsWith("analyzer:", StringComparison.Ordinal)).ToList();
            var typesDefined = await GetTypesDefinedAsync(document, ct).ConfigureAwait(false);

            files.Add(new FileValidationReport(
                normalized,
                resolver.GetProjectName(project.Id),
                typesDefined,
                errors,
                warnings,
                analyzer));
        }

        // 3. Symbol liveness.
        var symbolReports = new List<SymbolValidationReport>();
        if (symbols is { Count: > 0 })
        {
            foreach (var symbol in symbols.Distinct(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                symbolReports.Add(await CheckSymbolAsync(loaded, resolver, metadata, symbol, ct).ConfigureAwait(false));
            }
        }

        // 4. Baseline delta (only when a baseline is supplied).
        var allErrors = files.SelectMany(f => f.Errors).ToList();
        IReadOnlyList<DiagnosticInfo>? newErrors = null;
        IReadOnlyList<DiagnosticInfo>? resolvedErrors = null;
        if (baseline is not null)
        {
            (newErrors, resolvedErrors) = DiffBaseline(allErrors, baseline);
        }

        // 5. passed + message.
        var errorCount = allErrors.Count;
        var filesChecked = files.Count;
        // Stage 0 / item 3: `newErrors` is only populated on the baseline path (step 4), so
        // reading its Count unconditionally is a possible null dereference (CS8602). Tie the
        // read to the baseline check instead of relying on the caller's invariant.
        var noNewErrors = baseline is null || newErrors is { Count: 0 };
        var passed = errorCount == 0
                     && noNewErrors
                     && symbolReports.All(s => s.Resolved)
                     && !(filesChecked == 0 && skipped.Count > 0);

        var message = BuildMessage(filesChecked, skipped, errorCount, newErrors);

        var summary = new ValidateEditSummary(
            errorCount,
            files.Sum(f => f.Warnings.Count),
            filesChecked,
            symbolReports.Count,
            skipped,
            loaded.Degraded);

        return new ValidateEditResult(passed, files, newErrors, resolvedErrors, symbolReports, summary, message);
    }

    private static async Task<IReadOnlyList<string>> GetTypesDefinedAsync(Document document, CancellationToken ct)
    {
        var tree = await document.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
        if (tree is null)
            return [];
        var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
        return root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Select(t => t.Identifier.Text)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<SymbolValidationReport> CheckSymbolAsync(
        LoadedSolution loaded, SymbolResolver resolver, MetadataSymbolResolver metadata,
        string symbol, CancellationToken ct)
    {
        var sourceMatches = resolver.FindSymbols(symbol);
        IReadOnlyList<ISymbol> targets;
        if (sourceMatches.Count > 0)
        {
            targets = sourceMatches;
        }
        else
        {
            var meta = metadata.Resolve(symbol);
            if (meta is null)
            {
                // Unresolved: the edit likely removed/renamed something still mentioned textually.
                var simpleName = symbol.Contains('.') ? symbol[(symbol.LastIndexOf('.') + 1)..] : symbol;
                var dangling = FindDanglingCandidates(loaded, resolver, simpleName, DanglingLimit, ct);
                return new SymbolValidationReport(symbol, false, 0,
                    new Dictionary<string, int>(StringComparer.Ordinal), dangling);
            }
            targets = [meta.Symbol];
        }

        var refs = FindReferencesLogic.Execute(loaded, resolver, metadata, symbol);
        var byKind = refs
            .GroupBy(r => r.ReferenceKind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new SymbolValidationReport(symbol, true, refs.Count, byKind, []);
    }

    /// <summary>
    /// Files where the symbol's simple name still appears — as an identifier, in a comment,
    /// an XML-doc cref, or a string literal. A word-boundary text match over the (already
    /// in-memory) tree covers all of those; the compiler has already reported real references
    /// as errors, so this is a soft hint for the textual mentions it cannot see.
    /// </summary>
    private static List<string> FindDanglingCandidates(
        LoadedSolution loaded, SymbolResolver resolver, string simpleName, int limit, CancellationToken ct)
    {
        var pattern = new Regex($@"\b{Regex.Escape(simpleName)}\b", RegexOptions.Compiled);
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scan in SolutionScanner.EnumerateTrees(loaded, resolver, cancellationToken: ct))
        {
            ct.ThrowIfCancellationRequested();
            var path = scan.Tree.FilePath;
            if (path is null || !seen.Add(path))
                continue;
            if (pattern.IsMatch(scan.Tree.GetText(ct).ToString()))
            {
                candidates.Add(path);
                if (candidates.Count >= limit)
                    break;
            }
        }
        return candidates;
    }

    private static (IReadOnlyList<DiagnosticInfo> New, IReadOnlyList<DiagnosticInfo> Resolved) DiffBaseline(
        IReadOnlyList<DiagnosticInfo> currentErrors, ValidateEditBaseline baseline)
    {
        static (string, string, int, int, string) Key(DiagnosticInfo d) => (d.Id, d.File, d.Line, d.Column, d.Message);
        var currentKeys = new HashSet<(string, string, int, int, string)>(currentErrors.Select(Key));
        var baselineKeys = new HashSet<(string, string, int, int, string)>(baseline.Errors.Select(Key));
        var newErrors = currentErrors.Where(d => !baselineKeys.Contains(Key(d))).ToList();
        var resolvedErrors = baseline.Errors.Where(d => !currentKeys.Contains(Key(d))).ToList();
        return (newErrors, resolvedErrors);
    }

    private static string BuildMessage(
        int filesChecked, IReadOnlyList<string> skipped, int errorCount, IReadOnlyList<DiagnosticInfo>? newErrors)
    {
        if (filesChecked == 0 && skipped.Count > 0)
            return $"Edited file(s) are outside the loaded solution: {string.Join(", ", skipped)}. Roslyn cannot verify them.";
        if (errorCount > 0 || (newErrors is { Count: > 0 }))
            return $"{(errorCount > 0 ? errorCount : newErrors!.Count)} error(s) in edited file(s). Fix before continuing.";
        return "No new compiler errors. Rebuild not required.";
    }
}
