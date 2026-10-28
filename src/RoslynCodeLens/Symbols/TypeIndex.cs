using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens;

/// <summary>
/// Ответы про иерархию типов: кто реализует интерфейс, кто наследует базовый класс.
/// Находит типы по имени через <see cref="SymbolIndex"/>, но владеет только картами
/// наследования — сами типы и их имена его не касаются.
///
/// Выделено из <see cref="SymbolResolver"/> (этап 5.1 плана SOLID-аудита).
/// Индекс неизменяем: карты строятся в конструкторе по одному снимку решения.
/// </summary>
public sealed class TypeIndex
{
    private readonly SymbolIndex _symbols;
    private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> _interfaceImplementors;
    private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> _derivedTypes;

    public TypeIndex(LoadedSolution loaded)
        : this(loaded, new SymbolIndex(loaded))
    {
    }

    /// <summary>
    /// Builds the inheritance maps over an existing name index — avoids collecting and
    /// indexing every type twice when the caller already has a <see cref="SymbolIndex"/>.
    /// </summary>
    public TypeIndex(LoadedSolution loaded, SymbolIndex symbols)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(symbols);

        _symbols = symbols;
        (_interfaceImplementors, _derivedTypes) = BuildInheritanceMaps(symbols.AllTypes);
    }

    /// <summary>
    /// Type lookup by name — convenience delegate to <see cref="SymbolIndex.FindNamedTypes"/>.
    /// Callers holding only a hierarchy still need to turn a name into the type to query.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> FindNamedTypes(string symbol) => _symbols.FindNamedTypes(symbol);

    private static (Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>,
                     Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>)
        BuildInheritanceMaps(IReadOnlyList<INamedTypeSymbol> allTypes)
    {
        var implementors = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(NamedTypeSignatureComparer.Instance);
        var derived = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(NamedTypeSignatureComparer.Instance);

        // Plain foreach rather than CollectionsMarshal.AsSpan: the parameter is the index's
        // IReadOnlyList view, and this builds once per solution snapshot, so the span
        // fast-path buys nothing measurable.
        foreach (var type in allTypes)
        {
            foreach (var iface in type.AllInterfaces)
            {
                // Skip error symbols (e.g. NoPiaMissingCanonicalTypeSymbol from unresolved
                // embedded-interop types) — they have no real identity and would pollute the index.
                if (iface.TypeKind == TypeKind.Error) continue;
                if (!implementors.TryGetValue(iface, out var implList))
                {
                    implList = new List<INamedTypeSymbol>();
                    implementors[iface] = implList;
                }
                implList.Add(type);
            }

            var baseType = type.BaseType;
            while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
            {
                if (baseType.TypeKind == TypeKind.Error) break;
                if (!derived.TryGetValue(baseType, out var derivedList))
                {
                    derivedList = new List<INamedTypeSymbol>();
                    derived[baseType] = derivedList;
                }
                derivedList.Add(type);
                baseType = baseType.BaseType;
            }
        }

        return (implementors, derived);
    }

    /// <summary>
    /// Returns all types that implement the given interface, using pre-built index.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> GetInterfaceImplementors(INamedTypeSymbol iface)
    {
        return _interfaceImplementors.TryGetValue(iface, out var list) ? list : [];
    }

    /// <summary>
    /// Returns all types that derive from the given type (directly or transitively), using pre-built index.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> GetDerivedTypes(INamedTypeSymbol baseType)
    {
        return _derivedTypes.TryGetValue(baseType, out var list) ? list : [];
    }
}
