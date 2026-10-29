using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Tools;

internal static class DeadCodeFilters
{
    public enum Reason
    {
        None,
        TestMethod,
        TestContainer,
        McpTool,
        Generated,
        Composition,
        Interop,
    }

    private static readonly string[] TestMethodAttributes =
    [
        // xUnit
        "Fact", "Theory", "InlineData", "MemberData", "ClassData",
        // NUnit
        "Test", "TestCase", "TestCaseSource", "Values", "ValueSource", "Range",
        "Random", "Combinatorial", "Pairwise", "Sequential",
        "Datapoint", "DatapointSource",
        "SetUp", "TearDown", "OneTimeSetUp", "OneTimeTearDown",
        // MSTest
        "TestMethod", "DataTestMethod", "DataRow", "DynamicData",
        "TestInitialize", "TestCleanup",
        "ClassInitialize", "ClassCleanup",
        "AssemblyInitialize", "AssemblyCleanup",
    ];

    private static readonly string[] TestContainerAttributes =
    [
        "TestClass", "TestFixture", "TestFixtureSource",
        "Collection", "CollectionDefinition",
    ];

    private static readonly string[] McpAttributes =
    [
        "McpServerTool", "McpServerToolType",
    ];

    private static readonly string[] GeneratedAttributes =
    [
        "CompilerGenerated", "GeneratedCode", "DebuggerNonUserCode",
    ];

    private static readonly string[] CompositionAttributes =
    [
        "Export", "InheritedExport", "Import", "ImportMany", "ImportingConstructor",
    ];

    private static readonly string[] InteropFieldAttributes =
    [
        "FieldOffset", "MarshalAs",
    ];

    private static readonly string[] InteropStructAttributes =
    [
        "StructLayout", "InlineArray",
    ];

    /// <summary>
    /// Classifies a symbol into the first matching skip reason. Order is significant: it is the
    /// priority ladder, and a symbol matching two families reports the earlier one.
    /// </summary>
    public static Reason Classify(ISymbol symbol)
    {
        // MCP tools and generated code are recognised either on the symbol itself or on the type
        // that declares it — a partial class carries the attribute once, and every member inherits
        // the classification.
        if (AttributeOnSelfOrType(symbol, McpAttributes)) return Reason.McpTool;
        if (AttributeOnSelfOrType(symbol, GeneratedAttributes)) return Reason.Generated;

        if (IsTestMethod(symbol)) return Reason.TestMethod;
        if (IsInTestContainer(symbol)) return Reason.TestContainer;

        if (AttributeOnSelfOrType(symbol, CompositionAttributes)) return Reason.Composition;

        // Interop is field-shaped: the field carries a marshalling attribute, or its type is a
        // struct with an explicit layout.
        if (symbol is IFieldSymbol interopField && IsInterop(interopField)) return Reason.Interop;

        return Reason.None;
    }

    private static bool AttributeOnSelfOrType(ISymbol symbol, string[] attributeNames) =>
        HasAnyAttribute(symbol, attributeNames)
        || (symbol.ContainingType != null && HasAnyAttribute(symbol.ContainingType, attributeNames));

    private static bool IsTestMethod(ISymbol symbol) =>
        symbol is IMethodSymbol && HasAnyAttribute(symbol, TestMethodAttributes);

    private static bool IsInterop(IFieldSymbol field) =>
        HasAnyAttribute(field, InteropFieldAttributes)
        || (field.ContainingType != null && HasAnyAttribute(field.ContainingType, InteropStructAttributes));

    private static bool IsInTestContainer(ISymbol symbol)
    {
        for (var type = symbol as INamedTypeSymbol ?? symbol.ContainingType; type != null; type = type.BaseType)
        {
            if (HasAnyAttribute(type, TestContainerAttributes))
                return true;

            foreach (var member in type.GetMembers())
            {
                if (member is IMethodSymbol m && HasAnyAttribute(m, TestMethodAttributes))
                    return true;
            }
        }
        return false;
    }

    private static bool HasAnyAttribute(ISymbol symbol, string[] names)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            for (var cls = attr.AttributeClass; cls != null; cls = cls.BaseType)
            {
                var simple = cls.Name;
                var simpleNoSuffix = simple.EndsWith("Attribute", StringComparison.Ordinal)
                    ? simple[..^"Attribute".Length]
                    : simple;
                foreach (var name in names)
                {
                    if (string.Equals(simple, name, StringComparison.Ordinal)) return true;
                    if (string.Equals(simple, name + "Attribute", StringComparison.Ordinal)) return true;
                    if (string.Equals(simpleNoSuffix, name, StringComparison.Ordinal)) return true;
                }
            }
        }
        return false;
    }
}
