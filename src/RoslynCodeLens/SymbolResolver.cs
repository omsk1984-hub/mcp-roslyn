using Microsoft.CodeAnalysis;

namespace RoslynCodeLens;

/// <summary>
/// Фасад над тремя узкими индексами решения:
/// <see cref="SymbolIndex"/> (поиск по имени), <see cref="TypeIndex"/> (иерархии и реализации)
/// и <see cref="LocationIndex"/> (файл, строка, проект, признак сгенерированного кода).
///
/// До этапа 5.1 плана SOLID-аудита всё это были 45 членов и 13 полей одного класса
/// (414 LOC) — единственный god-объект решения. Разбиение — чистый перенос кода:
/// публичная поверхность не изменилась, потому что у <c>SymbolResolver</c> 218 ссылок
/// в трёх проектах, и менять все вызывающие ради формы было бы неоправданным риском.
///
/// Новый код должен зависеть от того индекса, который ему действительно нужен,
/// а не от фасада. Фасад существует для существующих вызывающих и может быть
/// удалён, когда их переведут на индексы напрямую.
/// </summary>
public class SymbolResolver
{
    private readonly SymbolIndex _symbols;
    private readonly TypeIndex _types;
    private readonly LocationIndex _locations;

    public SymbolResolver(LoadedSolution loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);

        // One type walk feeds both the name index and the inheritance maps; building
        // them from a shared SymbolIndex keeps construction at its previous cost.
        _symbols = new SymbolIndex(loaded);
        _types = new TypeIndex(loaded, _symbols);
        _locations = new LocationIndex(loaded);
    }

    /// <summary>Name lookup component: types, members, events, attributes.</summary>
    public SymbolIndex Symbols => _symbols;

    /// <summary>Type hierarchy component: interface implementors, derived types.</summary>
    public TypeIndex Types => _types;

    /// <summary>Location component: file, line, project, generated-code and markup mapping.</summary>
    public LocationIndex Locations => _locations;

    /// <summary>
    /// All deduplicated types across all compilations, cached at construction.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> AllTypes => _symbols.AllTypes;

    public IReadOnlyDictionary<string, List<INamedTypeSymbol>> TypesBySimpleName => _symbols.TypesBySimpleName;
    public IReadOnlyDictionary<string, List<ISymbol>> MembersBySimpleName => _symbols.MembersBySimpleName;
    public IReadOnlyDictionary<string, List<(ISymbol Symbol, AttributeData Attribute)>> AttributeIndex => _symbols.AttributeIndex;

    public IReadOnlyList<INamedTypeSymbol> FindNamedTypes(string symbol) => _symbols.FindNamedTypes(symbol);

    public IReadOnlyList<IMethodSymbol> FindMethods(string symbol) => _symbols.FindMethods(symbol);

    public IReadOnlyList<IEventSymbol> FindEvents(string symbol) => _symbols.FindEvents(symbol);

    public IReadOnlyList<ISymbol> FindSymbols(string symbol) => _symbols.FindSymbols(symbol);

    /// <inheritdoc cref="SymbolIndex.GetAllTypes(INamespaceSymbol)"/>
    public static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns) => SymbolIndex.GetAllTypes(ns);

    /// <inheritdoc cref="LocationIndex.GetLocation(ISymbol)"/>
    public static Location? GetLocation(ISymbol symbol) => LocationIndex.GetLocation(symbol);

    public (string File, int Line) GetFileAndLine(ISymbol symbol) => _locations.GetFileAndLine(symbol);

    public string GetProjectName(ISymbol symbol) => _locations.GetProjectName(symbol);

    public string GetProjectName(ProjectId projectId) => _locations.GetProjectName(projectId);

    /// <summary>
    /// Returns all types that implement the given interface, using pre-built index.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> GetInterfaceImplementors(INamedTypeSymbol iface) =>
        _types.GetInterfaceImplementors(iface);

    /// <summary>
    /// Returns all types that derive from the given type (directly or transitively), using pre-built index.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> GetDerivedTypes(INamedTypeSymbol baseType) =>
        _types.GetDerivedTypes(baseType);

    public string? GetMarkupSource(string? filePath) => _locations.GetMarkupSource(filePath);

    public bool IsGenerated(string? filePath) => _locations.IsGenerated(filePath);
}
