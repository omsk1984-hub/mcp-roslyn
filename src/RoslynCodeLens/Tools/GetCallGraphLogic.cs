using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class GetCallGraphLogic
{
    private static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMemberOptions(
            SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeExplicitInterface)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType);

    public static async Task<GetCallGraphResult?> ExecuteAsync(
        LoadedSolution loaded,
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string symbol,
        string direction,
        int maxDepth,
        int maxNodes,
        CancellationToken cancellationToken = default)
    {
        if (direction is not ("callees" or "callers" or "both"))
            throw new ArgumentException(
                $"Invalid direction '{direction}'. Expected 'callees', 'callers', or 'both'.",
                nameof(direction));

        var methods = resolver.FindMethods(symbol);
        if (methods.Count == 0) return null;

        var root = methods[0];
        var rootFqn = Fqn(root);

        var treeToCompilation = new Dictionary<SyntaxTree, Compilation>();
        foreach (var (_, comp) in loaded.Compilations)
            foreach (var tree in comp.SyntaxTrees)
                treeToCompilation[tree] = comp;

        var callees = new Dictionary<string, CallGraphNode>(StringComparer.Ordinal);
        var callers = new Dictionary<string, CallGraphNode>(StringComparer.Ordinal);
        var truncated = false;

        if (direction is "callees" or "both")
            truncated |= WalkCallees(loaded, treeToCompilation, resolver, root, rootFqn, maxDepth, maxNodes, cancellationToken, callees, callers);

        if (direction is "callers" or "both")
            truncated |= await WalkCallersAsync(loaded, resolver, root, rootFqn, maxDepth, maxNodes, callers, callees, cancellationToken).ConfigureAwait(false);

        return new GetCallGraphResult(
            Root: rootFqn,
            Direction: direction,
            MaxDepthRequested: maxDepth,
            Truncated: truncated,
            Callees: callees,
            Callers: callers);
    }

    private static bool WalkCallees(
        LoadedSolution loaded,
        IReadOnlyDictionary<SyntaxTree, Compilation> treeToCompilation,
        SymbolResolver resolver,
        IMethodSymbol root,
        string rootFqn,
        int maxDepth,
        int maxNodes,
        CancellationToken cancellationToken,
        Dictionary<string, CallGraphNode> map,
        Dictionary<string, CallGraphNode> otherMap)
    {
        var queue = new Queue<(IMethodSymbol Sym, int Depth)>();
        queue.Enqueue((root, 0));
        map[rootFqn] = BuildNode(resolver, root, edges: []);

        var truncated = false;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            var currentFqn = Fqn(current);
            var edges = new List<CallGraphEdge>();

            foreach (var (callee, edgeKind) in EnumerateOutgoingCalls(treeToCompilation, current, cancellationToken))
            {
                var calleeFqn = Fqn(callee);

                if (!map.ContainsKey(calleeFqn))
                {
                    if (map.Count + otherMap.Count >= maxNodes)
                    {
                        truncated = true;
                        // edge to truncated target still recorded below
                    }
                    else
                    {
                        var isExternal = !callee.Locations.Any(l => l.IsInSource);
                        map[calleeFqn] = BuildNode(resolver, callee, edges: [], forceExternal: isExternal);

                        if (!isExternal)
                            queue.Enqueue((callee, depth + 1));
                    }
                }

                edges.Add(new CallGraphEdge(calleeFqn, edgeKind));
            }

            map[currentFqn] = map[currentFqn] with { Edges = edges };
        }

        return truncated;
    }

    private static async Task<bool> WalkCallersAsync(
        LoadedSolution loaded,
        SymbolResolver resolver,
        IMethodSymbol root,
        string rootFqn,
        int maxDepth,
        int maxNodes,
        Dictionary<string, CallGraphNode> map,
        Dictionary<string, CallGraphNode> otherMap,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<(IMethodSymbol Sym, int Depth)>();
        queue.Enqueue((root, 0));
        map[rootFqn] = BuildNode(resolver, root, edges: []);

        var truncated = false;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            var currentFqn = Fqn(current);
            var edges = new List<CallGraphEdge>();

            var callerInfos = await SymbolFinder.FindCallersAsync(current, loaded.Solution, cancellationToken)
                .ConfigureAwait(false);

            foreach (var info in callerInfos)
            {
                if (info.CallingSymbol is not IMethodSymbol caller) continue;

                var callerFqn = Fqn(caller);
                if (!map.ContainsKey(callerFqn))
                {
                    if (map.Count + otherMap.Count >= maxNodes)
                    {
                        truncated = true;
                        // edge to truncated target still recorded below
                    }
                    else
                    {
                        map[callerFqn] = BuildNode(resolver, caller, edges: []);
                        queue.Enqueue((caller, depth + 1));
                    }
                }

                edges.Add(new CallGraphEdge(callerFqn, EdgeKindFor(caller)));
            }

            map[currentFqn] = map[currentFqn] with { Edges = edges };
        }

        return truncated;
    }

    /// <summary>
    /// Every distinct outgoing call in a method body, as (callee, edge kind) pairs in first-seen
    /// order.
    /// </summary>
    /// <remarks>
    /// Split in two because they answer different questions. <see cref="ResolveCallee"/> decides
    /// <i>which</i> symbol a syntax node calls and under which edge kind — a table of shapes, one
    /// per node kind, that changes whenever the graph learns a new call form. This method only
    /// walks the body and dedupes, which changes never. Methods with no source location (metadata
    /// only) have no body to walk and yield nothing — that is what makes BCL/NuGet callees
    /// terminal leaves in <see cref="WalkCallees"/>.
    /// </remarks>
    private static IEnumerable<(IMethodSymbol Callee, CallGraphEdgeKind EdgeKind)> EnumerateOutgoingCalls(
        IReadOnlyDictionary<SyntaxTree, Compilation> treeToCompilation,
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        var location = method.Locations.FirstOrDefault(l => l.IsInSource);
        if (location?.SourceTree is null) yield break;

        if (!treeToCompilation.TryGetValue(location.SourceTree, out var compilation))
            yield break;

        var semanticModel = compilation.GetSemanticModel(location.SourceTree);
        var bodyNode = location.SourceTree.GetRoot().FindNode(location.SourceSpan);

        var seen = new HashSet<(string, CallGraphEdgeKind)>();

        foreach (var node in bodyNode.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ResolveCallee(node, semanticModel, out var called, out var kind)) continue;

            var dedupKey = (Fqn(called), kind);
            if (!seen.Add(dedupKey)) continue;
            yield return (called, kind);
        }
    }

    /// <summary>
    /// The symbol a single syntax node calls and its edge kind, or false when the node expresses
    /// no call. One case per call shape: the cases are independent and mutually exclusive by
    /// construction (a node is exactly one syntax kind), so their order carries no meaning.
    /// </summary>
    private static bool ResolveCallee(
        SyntaxNode node, SemanticModel semanticModel, out IMethodSymbol callee, out CallGraphEdgeKind kind)
    {
        callee = null!;
        kind = CallGraphEdgeKind.Method;

        switch (node)
        {
            case InvocationExpressionSyntax inv:
                return TryInvocation(inv, semanticModel, out callee, out kind);

            case ObjectCreationExpressionSyntax oc:
                return TryConstructor(oc, semanticModel, out callee, out kind);

            case ImplicitObjectCreationExpressionSyntax ioc:
                return TryConstructor(ioc, semanticModel, out callee, out kind);

            case ConstructorInitializerSyntax ci:
                return TryConstructor(ci, semanticModel, out callee, out kind);

            case AssignmentExpressionSyntax asg:
                return TryPropertySet(asg, semanticModel, out callee, out kind);

            case MemberAccessExpressionSyntax ma:
                return TryPropertyGet(ma, semanticModel, out callee, out kind);

            case BinaryExpressionSyntax bin:
                return TryBinaryOperator(bin, semanticModel, out callee, out kind);

            default:
                return false;
        }
    }

    /// <summary>
    /// An invocation: operators called directly (<c>a + b</c> written as <c>op_Addition(a, b)</c>)
    /// keep the operator edge kind, everything else is a plain method call.
    /// </summary>
    private static bool TryInvocation(
        InvocationExpressionSyntax inv, SemanticModel semanticModel,
        out IMethodSymbol callee, out CallGraphEdgeKind kind)
    {
        kind = CallGraphEdgeKind.Method;
        callee = null!;
        if (semanticModel.GetSymbolInfo(inv).Symbol is not IMethodSymbol m) return false;

        callee = m;
        kind = m.MethodKind == MethodKind.UserDefinedOperator
            ? CallGraphEdgeKind.Operator
            : CallGraphEdgeKind.Method;
        return true;
    }

    /// <summary>
    /// Any construction form — explicit <c>new T()</c>, target-typed <c>new()</c>, and a
    /// <c>: base(...)</c>/<c>: this(...)</c> initializer — is a constructor edge.
    /// </summary>
    private static bool TryConstructor(
        SyntaxNode creation, SemanticModel semanticModel,
        out IMethodSymbol callee, out CallGraphEdgeKind kind)
    {
        kind = CallGraphEdgeKind.Constructor;
        callee = null!;
        if (semanticModel.GetSymbolInfo(creation).Symbol is not IMethodSymbol ctor) return false;

        callee = ctor;
        return true;
    }

    /// <summary>An assignment writes through the property's setter.</summary>
    private static bool TryPropertySet(
        AssignmentExpressionSyntax asg, SemanticModel semanticModel,
        out IMethodSymbol callee, out CallGraphEdgeKind kind)
    {
        kind = CallGraphEdgeKind.PropertySet;
        callee = null!;
        if (semanticModel.GetSymbolInfo(asg.Left).Symbol is not IPropertySymbol setProp) return false;
        if (setProp.SetMethod is not IMethodSymbol setter) return false;

        callee = setter;
        return true;
    }

    /// <summary>
    /// A member access reads through the property's getter — unless it is the target of a plain
    /// assignment, which is the setter's and already counted there.
    /// </summary>
    private static bool TryPropertyGet(
        MemberAccessExpressionSyntax ma, SemanticModel semanticModel,
        out IMethodSymbol callee, out CallGraphEdgeKind kind)
    {
        kind = CallGraphEdgeKind.PropertyGet;
        callee = null!;
        if (semanticModel.GetSymbolInfo(ma).Symbol is not IPropertySymbol getProp) return false;
        if (IsPropertyWriteContext(ma)) return false;
        if (getProp.GetMethod is not IMethodSymbol getter) return false;

        callee = getter;
        return true;
    }

    /// <summary>
    /// A binary expression over a user-defined operator. Built-in operators bind to no method and
    /// fall through here — they are not edges.
    /// </summary>
    private static bool TryBinaryOperator(
        BinaryExpressionSyntax bin, SemanticModel semanticModel,
        out IMethodSymbol callee, out CallGraphEdgeKind kind)
    {
        kind = CallGraphEdgeKind.Operator;
        callee = null!;
        if (semanticModel.GetSymbolInfo(bin).Symbol is not IMethodSymbol op) return false;
        if (op.MethodKind != MethodKind.UserDefinedOperator) return false;

        callee = op;
        return true;
    }

    private static bool IsPropertyWriteContext(MemberAccessExpressionSyntax ma)
        => ma.Parent is AssignmentExpressionSyntax asg
           && asg.Left == ma
           && asg.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression);

    private static CallGraphNode BuildNode(
        SymbolResolver resolver,
        IMethodSymbol symbol,
        IReadOnlyList<CallGraphEdge> edges,
        bool forceExternal = false)
    {
        var isExternal = forceExternal || !symbol.Locations.Any(l => l.IsInSource);
        var (file, line) = isExternal ? ("", 0) : resolver.GetFileAndLine(symbol);
        var project = isExternal ? "" : resolver.GetProjectName(symbol);

        return new CallGraphNode(
            Kind: NodeKindFor(symbol),
            Project: project,
            FilePath: file,
            Line: line,
            IsExternal: isExternal,
            Edges: edges);
    }

    private static CallGraphNodeKind NodeKindFor(IMethodSymbol symbol)
        => symbol.MethodKind switch
        {
            MethodKind.Constructor => CallGraphNodeKind.Constructor,
            MethodKind.UserDefinedOperator or MethodKind.Conversion => CallGraphNodeKind.Operator,
            MethodKind.PropertyGet or MethodKind.PropertySet => CallGraphNodeKind.Property,
            _ => CallGraphNodeKind.Method
        };

    private static CallGraphEdgeKind EdgeKindFor(IMethodSymbol symbol)
        => symbol.MethodKind switch
        {
            MethodKind.Constructor => CallGraphEdgeKind.Constructor,
            MethodKind.UserDefinedOperator or MethodKind.Conversion => CallGraphEdgeKind.Operator,
            MethodKind.PropertyGet => CallGraphEdgeKind.PropertyGet,
            MethodKind.PropertySet => CallGraphEdgeKind.PropertySet,
            _ => CallGraphEdgeKind.Method
        };

    private static string Fqn(ISymbol symbol)
        => symbol.ToDisplayString(FqnFormat).Replace("global::", string.Empty, StringComparison.Ordinal);
}
