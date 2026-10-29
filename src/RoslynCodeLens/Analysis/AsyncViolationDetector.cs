using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Analysis;

/// <summary>
/// Everything a detector needs to know about the method currently being inspected.
/// </summary>
internal sealed class AsyncMethodScan
{
    internal AsyncMethodScan(
        MethodDeclarationSyntax methodDecl,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        AsyncViolationDetector.TaskSymbols symbols,
        string projectName)
    {
        Declaration = methodDecl;
        SemanticModel = semanticModel;
        Symbols = symbols;
        ProjectName = projectName;
        MethodSymbol = methodSymbol;

        ContainingMethodName = methodSymbol.ContainingType is null
            ? methodSymbol.Name
            : $"{methodSymbol.ContainingType.Name}.{methodSymbol.Name}";
        IsAsync = methodDecl.Modifiers.Any(SyntaxKind.AsyncKeyword);

        // A method may legitimately have no body (abstract, extern, partial declaration);
        // only the async-void pattern is meaningful then.
        Body = (SyntaxNode?)methodDecl.Body ?? methodDecl.ExpressionBody;
    }

    internal MethodDeclarationSyntax Declaration { get; }

    internal IMethodSymbol MethodSymbol { get; }

    internal SemanticModel SemanticModel { get; }

    internal AsyncViolationDetector.TaskSymbols Symbols { get; }

    internal string ProjectName { get; }

    internal string ContainingMethodName { get; }

    internal bool IsAsync { get; }

    internal SyntaxNode? Body { get; }
}

/// <summary>
/// Detects the six classes of async/await misuse this server reports, one method per class.
/// </summary>
/// <remarks>
/// Each pattern has its own detector, so a change to one class of violation cannot silently
/// change another, and the aggregate scan below stays a plain aggregation:
/// <list type="bullet">
/// <item><see cref="DetectAsyncVoidInMethod"/> — <c>async void</c> outside an event handler;</item>
/// <item><see cref="DetectSyncOverAsyncResultInMethod"/> — blocking read of a Task/ValueTask result;</item>
/// <item><see cref="DetectSyncOverAsyncWaitInMethod"/> — <c>Task.Wait</c>/<c>WaitAll</c>/<c>WaitAny</c>;</item>
/// <item><see cref="DetectSyncOverAsyncGetAwaiterGetResultInMethod"/> — <c>GetAwaiter().GetResult()</c>;</item>
/// <item><see cref="DetectMissingAwaitInMethod"/> — unawaited call in an <c>async</c> method;</item>
/// <item><see cref="DetectFireAndForgetInMethod"/> — unawaited call in a non-<c>async</c> method.</item>
/// </list>
/// </remarks>
internal static class AsyncViolationDetector
{
    private const int SnippetMaxLength = 80;

    /// <summary>
    /// The Task-family symbols a detector needs, resolved once per compilation.
    /// <see cref="TaskSymbol"/> is never null: compilations that cannot resolve
    /// <c>System.Threading.Tasks.Task</c> are skipped before any of their trees are walked,
    /// because nothing here can be recognised without it.
    /// </summary>
    internal readonly record struct TaskSymbols(
        INamedTypeSymbol TaskSymbol,
        INamedTypeSymbol? TaskGenericSymbol,
        INamedTypeSymbol? ValueTaskSymbol,
        INamedTypeSymbol? ValueTaskGenericSymbol,
        INamedTypeSymbol? EventArgsSymbol)
    {
        internal static TaskSymbols Resolve(Compilation compilation) => new(
            compilation.GetTypeByMetadataName("System.Threading.Tasks.Task")!,
            compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1"),
            compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask"),
            compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1"),
            compilation.GetTypeByMetadataName("System.EventArgs"));
    }

    // ---- Aggregation ---------------------------------------------------------------------

    /// <summary>
    /// Runs every detector over the whole solution. The returned list is unordered — sorting and
    /// summarising is the caller's job.
    /// </summary>
    /// <remarks>
    /// Both exclusions are made in the projectFilter, which drops a whole compilation before any
    /// of its trees are touched — never inside the loop. The scanner's dedupe is first-one-wins,
    /// so a tree rejected AFTER it has claimed its dedupe slot is lost from every other
    /// compilation holding it too: a linked file shared with a test project would disappear from
    /// the production one, and a file in a project whose references failed to load (a real
    /// MSBuildWorkspace failure mode) would disappear from the healthy project that also has it.
    /// Test projects are excluded by ID, not name, because a name does not identify a project —
    /// two in different folders can share one, and a name-based exclusion would drop both.
    /// </remarks>
    internal static List<AsyncViolation> DetectAll(LoadedSolution loaded, SymbolResolver source)
    {
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution).ToHashSet();

        // Nothing this tool looks for can be recognised without System.Threading.Tasks.Task, so a
        // compilation that cannot resolve it is skipped in full rather than per-tree.
        var projectsWithoutTask = loaded.Compilations
            .Where(p => p.Value.GetTypeByMetadataName("System.Threading.Tasks.Task") is null)
            .Select(p => p.Key)
            .ToHashSet();

        var violations = new List<AsyncViolation>();

        // Which trees to walk — once each, generated ones skipped, project attribution
        // deterministic — is SolutionScanner's job; the loop below only asks what each node is.
        foreach (var scan in SolutionScanner.EnumerateTrees(
                     loaded, source,
                     projectFilter: (id, _) =>
                         !testProjectIds.Contains(id) && !projectsWithoutTask.Contains(id)))
        {
            var symbols = TaskSymbols.Resolve(scan.Compilation);

            // The model must come from the tree's OWN compilation, which is what the scan hands
            // back; binding a tree against any other would resolve nothing.
            var semanticModel = scan.SemanticModel();

            foreach (var methodDecl in scan.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
                if (methodSymbol is null || methodSymbol.IsImplicitlyDeclared) continue;

                var method = new AsyncMethodScan(methodDecl, methodSymbol, semanticModel, symbols, scan.ProjectName);
                violations.AddRange(DetectInMethod(method));
            }
        }

        return violations;
    }

    /// <summary>
    /// Runs every pattern detector against a single method.
    /// </summary>
    internal static List<AsyncViolation> DetectInMethod(AsyncMethodScan method)
    {
        var violations = new List<AsyncViolation>();

        violations.AddRange(DetectAsyncVoidInMethod(method));

        // Every other pattern needs a body to inspect.
        if (method.Body is null) return violations;

        violations.AddRange(DetectSyncOverAsyncResultInMethod(method));
        violations.AddRange(DetectSyncOverAsyncWaitInMethod(method));
        violations.AddRange(DetectSyncOverAsyncGetAwaiterGetResultInMethod(method));
        violations.AddRange(DetectMissingAwaitInMethod(method));
        violations.AddRange(DetectFireAndForgetInMethod(method));

        return violations;
    }

    /// <summary>
    /// Runs every detector over the whole solution and keeps only the given pattern. Used by tests
    /// to prove each pattern detector and the aggregate scan report the same violations.
    /// </summary>
    internal static List<AsyncViolation> DetectOnly(
        LoadedSolution loaded,
        SymbolResolver source,
        AsyncViolationPattern pattern) =>
        DetectAll(loaded, source).Where(v => v.Pattern == pattern).ToList();

    // ---- Pattern 4: async void -----------------------------------------------------------

    /// <summary>
    /// Pattern 4 — <c>async void</c> that is not shaped like an event handler.
    /// </summary>
    internal static IEnumerable<AsyncViolation> DetectAsyncVoidInMethod(AsyncMethodScan method)
    {
        if (!IsAsyncVoid(method.Declaration)) yield break;
        if (IsEventHandlerShaped(method.MethodSymbol, method.Symbols.EventArgsSymbol)) yield break;

        yield return BuildViolation(
            AsyncViolationPattern.AsyncVoid,
            AsyncViolationSeverity.Error,
            method.Declaration.Identifier.GetLocation(),
            method,
            "async void " + method.Declaration.Identifier.Text + "(...)");
    }

    private static bool IsAsyncVoid(MethodDeclarationSyntax methodDecl)
    {
        if (!methodDecl.Modifiers.Any(SyntaxKind.AsyncKeyword)) return false;
        return methodDecl.ReturnType is PredefinedTypeSyntax pts &&
               pts.Keyword.IsKind(SyntaxKind.VoidKeyword);
    }

    private static bool IsEventHandlerShaped(IMethodSymbol method, INamedTypeSymbol? eventArgsSymbol)
    {
        if (eventArgsSymbol is null) return false;
        if (method.Parameters.Length != 2) return false;

        var firstParam = method.Parameters[0].Type;
        var firstOk = firstParam.SpecialType == SpecialType.System_Object ||
                      firstParam.TypeKind == TypeKind.Class ||
                      firstParam.TypeKind == TypeKind.Interface;
        if (!firstOk) return false;

        return InheritsFromOrIs(method.Parameters[1].Type, eventArgsSymbol);
    }

    private static bool InheritsFromOrIs(ITypeSymbol type, INamedTypeSymbol baseType)
    {
        var current = type as INamedTypeSymbol;
        while (current is not null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType)) return true;
            current = current.BaseType;
        }
        return false;
    }

    // ---- Pattern 1: blocking on .Result --------------------------------------------------

    /// <summary>
    /// Pattern 1 — <c>.Result</c> read off a <c>Task</c>, <c>Task<T></c> or
    /// <c>ValueTask<T></c>.
    /// </summary>
    internal static IEnumerable<AsyncViolation> DetectSyncOverAsyncResultInMethod(AsyncMethodScan method)
    {
        foreach (var memberAccess in method.Body!.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (!IsResultMemberAccess(memberAccess)) continue;

            var receiverType = method.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
            if (!HasTaskResultProperty(receiverType, method.Symbols)) continue;

            yield return BuildViolation(
                AsyncViolationPattern.SyncOverAsyncResult,
                AsyncViolationSeverity.Error,
                memberAccess.GetLocation(),
                method,
                Snippet(memberAccess.ToString()));
        }
    }

    private static bool IsResultMemberAccess(MemberAccessExpressionSyntax memberAccess) =>
        string.Equals(memberAccess.Name.Identifier.Text, "Result") &&
        memberAccess.Parent is not InvocationExpressionSyntax;

    private static bool HasTaskResultProperty(ITypeSymbol? type, TaskSymbols symbols)
    {
        if (type is null) return false;
        if (SymbolEqualityComparer.Default.Equals(type, symbols.TaskSymbol)) return true;
        return IsConstructedFrom(type, symbols.TaskGenericSymbol) ||
               IsConstructedFrom(type, symbols.ValueTaskGenericSymbol);
    }

    // ---- Pattern 2: Task.Wait / WaitAll / WaitAny ----------------------------------------

    /// <summary>
    /// Pattern 2 — <c>Task.Wait</c>, <c>Task.WaitAll</c>, <c>Task.WaitAny</c>.
    /// </summary>
    internal static IEnumerable<AsyncViolation> DetectSyncOverAsyncWaitInMethod(AsyncMethodScan method)
    {
        foreach (var invocation in method.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (ResolveMethod(method.SemanticModel, invocation) is not { } symbol) continue;
            if (!IsTaskWaitMethod(symbol, method.Symbols.TaskSymbol)) continue;

            yield return BuildViolation(
                AsyncViolationPattern.SyncOverAsyncWait,
                AsyncViolationSeverity.Error,
                invocation.GetLocation(),
                method,
                Snippet(invocation.ToString()));
        }
    }

    private static IMethodSymbol? ResolveMethod(SemanticModel semanticModel, InvocationExpressionSyntax invocation) =>
        semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

    private static bool IsTaskWaitMethod(IMethodSymbol method, INamedTypeSymbol taskSymbol)
    {
        var containing = method.ContainingType?.OriginalDefinition;
        if (!SymbolEqualityComparer.Default.Equals(containing, taskSymbol)) return false;
        return method.Name is "Wait" or "WaitAll" or "WaitAny";
    }

    // ---- Pattern 3: GetAwaiter().GetResult() ---------------------------------------------

    /// <summary>
    /// Pattern 3 — <c>GetAwaiter().GetResult()</c>, including the configured variants.
    /// </summary>
    internal static IEnumerable<AsyncViolation> DetectSyncOverAsyncGetAwaiterGetResultInMethod(AsyncMethodScan method)
    {
        foreach (var invocation in method.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (ResolveMethod(method.SemanticModel, invocation) is not { } symbol) continue;
            if (!IsGetResultOnAwaiter(invocation, symbol)) continue;

            yield return BuildViolation(
                AsyncViolationPattern.SyncOverAsyncGetAwaiterGetResult,
                AsyncViolationSeverity.Error,
                invocation.GetLocation(),
                method,
                Snippet(invocation.ToString()));
        }
    }

    private static bool IsGetResultOnAwaiter(InvocationExpressionSyntax invocation, IMethodSymbol symbol)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return false;
        if (!string.Equals(memberAccess.Name.Identifier.Text, "GetResult")) return false;

        var containingType = symbol.ContainingType?.OriginalDefinition;
        if (containingType is null) return false;

        var fqn = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);

        return fqn is "System.Runtime.CompilerServices.TaskAwaiter"
            or "System.Runtime.CompilerServices.TaskAwaiter<TResult>"
            or "System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter"
            or "System.Runtime.CompilerServices.ConfiguredTaskAwaitable<TResult>.ConfiguredTaskAwaiter"
            or "System.Runtime.CompilerServices.ValueTaskAwaiter"
            or "System.Runtime.CompilerServices.ValueTaskAwaiter<TResult>"
            or "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter"
            or "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<TResult>.ConfiguredValueTaskAwaiter";
    }

    // ---- Patterns 5 & 6: unawaited invocations -------------------------------------------

    /// <summary>
    /// Pattern 5 — a <c>Task</c>-returning invocation used as a bare statement inside an
    /// <c>async</c> method.
    /// </summary>
    internal static IEnumerable<AsyncViolation> DetectMissingAwaitInMethod(AsyncMethodScan method) =>
        method.IsAsync
            ? DetectUnawaitedTaskStatements(method, AsyncViolationPattern.MissingAwait)
            : [];

    /// <summary>
    /// Pattern 6 — a <c>Task</c>-returning invocation used as a bare statement inside a
    /// non-<c>async</c> method.
    /// </summary>
    internal static IEnumerable<AsyncViolation> DetectFireAndForgetInMethod(AsyncMethodScan method) =>
        method.IsAsync
            ? []
            : DetectUnawaitedTaskStatements(method, AsyncViolationPattern.FireAndForget);

    private static IEnumerable<AsyncViolation> DetectUnawaitedTaskStatements(
        AsyncMethodScan method,
        AsyncViolationPattern pattern)
    {
        foreach (var stmt in method.Body!.DescendantNodes().OfType<ExpressionStatementSyntax>())
        {
            if (stmt.Expression is not InvocationExpressionSyntax invocation) continue;
            if (ResolveMethod(method.SemanticModel, invocation) is not { } symbol) continue;
            if (!IsTaskOrValueTaskType(symbol.ReturnType, method.Symbols)) continue;

            yield return BuildViolation(
                pattern,
                AsyncViolationSeverity.Warning,
                invocation.GetLocation(),
                method,
                Snippet(invocation.ToString()));
        }
    }

    private static bool IsTaskOrValueTaskType(ITypeSymbol type, TaskSymbols symbols)
    {
        if (SymbolEqualityComparer.Default.Equals(type, symbols.TaskSymbol)) return true;
        if (symbols.ValueTaskSymbol is not null &&
            SymbolEqualityComparer.Default.Equals(type, symbols.ValueTaskSymbol)) return true;

        return IsConstructedFrom(type, symbols.TaskGenericSymbol) ||
               IsConstructedFrom(type, symbols.ValueTaskGenericSymbol);
    }

    private static bool IsConstructedFrom(ITypeSymbol type, INamedTypeSymbol? constructedFrom) =>
        constructedFrom is not null &&
        type is INamedTypeSymbol { IsGenericType: true } named &&
        SymbolEqualityComparer.Default.Equals(named.ConstructedFrom, constructedFrom);

    // ---- Violation construction ----------------------------------------------------------

    private static string Snippet(string source) =>
        source.Length <= SnippetMaxLength ? source : source[..SnippetMaxLength] + "...";

    private static AsyncViolation BuildViolation(
        AsyncViolationPattern pattern,
        AsyncViolationSeverity severity,
        Location location,
        AsyncMethodScan method,
        string snippet)
    {
        var span = location.GetLineSpan();
        return new AsyncViolation(
            Pattern: pattern,
            Severity: severity,
            FilePath: span.Path ?? string.Empty,
            Line: span.StartLinePosition.Line + 1,
            ContainingMethod: method.ContainingMethodName,
            Project: method.ProjectName,
            Snippet: snippet);
    }
}
