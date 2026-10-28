using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ValidateEditTool
{
    private static readonly JsonSerializerOptions s_baselineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [McpServerTool(Name = "validate_edit"),
     Description("Read-only post-edit gate: one call answers 'did my edit break the solution?' — " +
                 "compiler/analyzer diagnostics per edited file, symbol liveness, and (optionally) a delta " +
                 "against a previous snapshot. Use it AFTER editing .cs files in the loaded solution, instead of " +
                 "the diagnostics → file overview → references chain. Do NOT use it for non-C# files, files outside " +
                 "the loaded solution, or after .csproj/props/NuGet changes (call rebuild_solution for those). " +
                 "Returns a single summary object with a 'passed' flag and a one-line 'message'.")]
    public static async Task<ValidateEditResult> Execute(
        MultiSolutionManager manager,
        Security.TrustStore trustStore,
        Security.AnalyzerAllowlist allowlist,
        [Description("Files the agent just edited (1..N), absolute paths")] string[] filePaths,
        [Description("Optional symbols whose references should be checked for dangling (type/member name)")]
            string[]? symbols = null,
        [Description("Minimum severity: 'error' (default) or 'warning'")] string? severity = null,
        [Description("Include analyzer diagnostics (default: false — requires trust_solution to be called first)")]
            bool includeAnalyzers = false,
        [Description("Optional path to a JSON snapshot of a previous validate_edit response; when given, the new/resolved error delta is computed")]
            string? baselineFile = null,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();

        var baseline = baselineFile is null
            ? null
            : await LoadBaselineAsync(baselineFile, ct).ConfigureAwait(false);

        return await ValidateEditLogic.ExecuteAsync(
            context.Loaded, context.Resolver,
            filePaths, symbols,
            severity ?? "error", includeAnalyzers,
            trustStore, allowlist,
            baseline, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a previous validate_edit response from disk and extracts its per-file errors as the
    /// baseline for the new/resolved delta. The agent saves the JSON itself (it has file tools);
    /// this tool only reads it.
    /// </summary>
    private static async Task<ValidateEditBaseline> LoadBaselineAsync(string baselineFile, CancellationToken ct)
    {
        if (!File.Exists(baselineFile))
            throw new McpToolException(
                ToolErrorCode.FileNotFound,
                $"Baseline file not found: {baselineFile}",
                new { baselineFile });

        var json = await File.ReadAllTextAsync(baselineFile, ct).ConfigureAwait(false);
        ValidateEditResult? previous;
        try
        {
            previous = JsonSerializer.Deserialize<ValidateEditResult>(json, s_baselineOptions);
        }
        catch (JsonException ex)
        {
            throw new McpToolException(
                ToolErrorCode.InvalidArgument,
                $"Baseline file is not a valid validate_edit JSON snapshot: {ex.Message}",
                new { baselineFile });
        }

        if (previous is null)
            throw new McpToolException(
                ToolErrorCode.InvalidArgument,
                $"Baseline file is empty or not a validate_edit response: {baselineFile}",
                new { baselineFile });

        var errors = previous.Files.SelectMany(f => f.Errors).ToList();
        return new ValidateEditBaseline(errors);
    }
}
