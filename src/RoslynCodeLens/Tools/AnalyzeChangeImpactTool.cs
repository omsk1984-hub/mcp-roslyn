using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class AnalyzeChangeImpactTool
{
    [McpServerTool(Name = "analyze_change_impact"),
     Description("Analyze the blast radius of changing a symbol — shows every file, project, and call site affected. " +
                 "Combines find_references and find_callers into a single impact summary. " +
                 "Use before renaming, changing signatures, or removing a type/method.")]
    public static Task<ChangeImpact?> ExecuteAsync(
        MultiSolutionManager manager,
        [Description("Symbol name to analyze (type name, 'Type.Method', etc.)")] string symbol,
        CancellationToken cancellationToken = default)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return AnalyzeChangeImpactLogic.ExecuteAsync(
            context.Loaded, context.Resolver, context.Metadata, symbol, cancellationToken);
    }
}
