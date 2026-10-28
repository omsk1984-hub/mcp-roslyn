using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens;

/// <summary>
/// Поиск символов по имени: типы (по простому, полному и arity-срезанному имени),
/// методы, события, члены и атрибуты. Отвечает за «найти по строке», но не за то,
/// где найденное лежит (это <see cref="LocationIndex"/>) и не за иерархии
/// (это <see cref="TypeIndex"/>).
///
/// Выделено из <see cref="SymbolResolver"/> (этап 5.1 плана SOLID-аудита).
/// Индекс неизменяем: всё строится в конструкторе по одному снимку решения.
/// </summary>
public sealed class SymbolIndex
{
    /// <summary>
    /// Fully qualified name without generic type parameters: renders
    /// "Data.Repository<T>" as "Data.Repository" and nested
    /// "Ns.Outer<T>.Inner" as "Ns.Outer.Inner".
    /// </summary>
    private static readonly SymbolDisplayFormat StrippedGenericsFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None);

    private readonly List<INamedTypeSymbol> _allTypes;
    private readonly Dictionary<string, List<INamedTypeSymbol>> _typesBySimpleName;
    private readonly Dictionary<string, INamedTypeSymbol> _typesByFullName;
    private readonly Dictionary<string, List<INamedTypeSymbol>> _typesByStrippedFullName;
    private readonly Dictionary<string, List<ISymbol>> _membersBySimpleName;
    private readonly Dictionary<string, List<(ISymbol Symbol, AttributeData Attribute)>> _attributeIndex;

    public SymbolIndex(LoadedSolution loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);

        _allTypes = CollectAllTypes(loaded);
        (_typesBySimpleName, _typesByFullName, _typesByStrippedFullName) = BuildTypeLookups(_allTypes);

        _membersBySimpleName = new Dictionary<string, List<ISymbol>>(StringComparer.OrdinalIgnoreCase);
        _attributeIndex = new Dictionary<string, List<(ISymbol, AttributeData)>>(StringComparer.OrdinalIgnoreCase);
        BuildMemberAndAttributeIndexes();
    }

    private static List<INamedTypeSymbol> CollectAllTypes(LoadedSolution loaded)
    {
        var allTypes = new List<INamedTypeSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var compilation in loaded.Compilations.Values)
        {
            foreach (var type in GetAllTypes(compilation.GlobalNamespace))
            {
                if (seen.Add(type.ToDisplayString()))
                    allTypes.Add(type);
            }
        }

        return allTypes;
    }

    private static (Dictionary<string, List<INamedTypeSymbol>>, Dictionary<string, INamedTypeSymbol>,
                     Dictionary<string, List<INamedTypeSymbol>>)
        BuildTypeLookups(List<INamedTypeSymbol> allTypes)
    {
        var bySimple = new Dictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);
        var byFull = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        var byStripped = new Dictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);

        foreach (ref readonly var type in CollectionsMarshal.AsSpan(allTypes))
        {
            var fullName = type.ToDisplayString();
            byFull[fullName] = type;

            // Generic types display with their type parameters ("Data.Repository<T>"),
            // so the documented qualified form "Data.Repository" misses byFull. Index
            // them under the arity-stripped name too; multiple arities share a key and
            // surface as multiple matches for the caller's ambiguity handling.
            var strippedName = type.ToDisplayString(StrippedGenericsFormat);
            if (!string.Equals(strippedName, fullName, StringComparison.Ordinal))
            {
                if (!byStripped.TryGetValue(strippedName, out var strippedList))
                {
                    strippedList = new List<INamedTypeSymbol>();
                    byStripped[strippedName] = strippedList;
                }
                strippedList.Add(type);
            }

            if (!bySimple.TryGetValue(type.Name, out var list))
            {
                list = new List<INamedTypeSymbol>();
                bySimple[type.Name] = list;
            }
            list.Add(type);
        }

        return (bySimple, byFull, byStripped);
    }

    private void BuildMemberAndAttributeIndexes()
    {
        foreach (ref readonly var type in CollectionsMarshal.AsSpan(_allTypes))
        {
            IndexAttributes(type);

            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared || string.IsNullOrEmpty(member.Name))
                    continue;

                IndexAttributes(member);

                if (member is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove })
                    continue;

                if (!_membersBySimpleName.TryGetValue(member.Name, out var memberList))
                {
                    memberList = new List<ISymbol>();
                    _membersBySimpleName[member.Name] = memberList;
                }
                memberList.Add(member);
            }
        }
    }

    private void IndexAttributes(ISymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            var attrName = attr.AttributeClass?.Name;
            if (string.IsNullOrEmpty(attrName))
                continue;

            // Index by full name (e.g., "ObsoleteAttribute")
            if (!_attributeIndex.TryGetValue(attrName, out var list))
            {
                list = new List<(ISymbol, AttributeData)>();
                _attributeIndex[attrName] = list;
            }
            list.Add((symbol, attr));

            // Also index by short name (e.g., "Obsolete")
            if (attrName.EndsWith("Attribute", StringComparison.Ordinal))
            {
                var shortName = attrName[..^"Attribute".Length];
                if (!_attributeIndex.TryGetValue(shortName, out var shortList))
                {
                    shortList = new List<(ISymbol, AttributeData)>();
                    _attributeIndex[shortName] = shortList;
                }
                shortList.Add((symbol, attr));
            }
        }
    }

    /// <summary>
    /// All deduplicated types across all compilations, cached at construction.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> AllTypes => _allTypes;

    public IReadOnlyDictionary<string, List<INamedTypeSymbol>> TypesBySimpleName => _typesBySimpleName;
    public IReadOnlyDictionary<string, List<ISymbol>> MembersBySimpleName => _membersBySimpleName;
    public IReadOnlyDictionary<string, List<(ISymbol Symbol, AttributeData Attribute)>> AttributeIndex => _attributeIndex;

    public IReadOnlyList<INamedTypeSymbol> FindNamedTypes(string symbol)
    {
        if (symbol.Contains('.', StringComparison.Ordinal))
        {
            if (_typesByFullName.TryGetValue(symbol, out var type))
                return [type];

            // Fall back to the arity-stripped index so the documented qualified form
            // ("Data.Repository") reaches generic types ("Data.Repository<T>").
            return _typesByStrippedFullName.TryGetValue(symbol, out var strippedMatches)
                ? strippedMatches
                : [];
        }

        return _typesBySimpleName.TryGetValue(symbol, out var list)
            ? list
            : [];
    }

    public IReadOnlyList<IMethodSymbol> FindMethods(string symbol)
    {
        var results = new List<IMethodSymbol>();
        var parts = symbol.Split('.');
        if (parts.Length < 2) return results;

        var typeName = string.Join('.', parts[..^1]);
        var methodName = parts[^1];

        foreach (var type in FindNamedTypes(typeName))
        {
            results.AddRange(type.GetMembers(methodName).OfType<IMethodSymbol>());
        }

        return results;
    }

    public IReadOnlyList<IEventSymbol> FindEvents(string symbol)
    {
        var results = new List<IEventSymbol>();
        var parts = symbol.Split('.');
        if (parts.Length < 2) return results;

        var typeName = string.Join('.', parts[..^1]);
        var eventName = parts[^1];

        foreach (var type in FindNamedTypes(typeName))
        {
            results.AddRange(type.GetMembers(eventName).OfType<IEventSymbol>());
        }

        return results;
    }

    public IReadOnlyList<ISymbol> FindMembers(string symbol)
    {
        var results = new List<ISymbol>();
        var parts = symbol.Split('.');
        if (parts.Length < 2) return results;

        var typeName = string.Join('.', parts[..^1]);
        var memberName = parts[^1];

        foreach (var type in FindNamedTypes(typeName))
        {
            foreach (var m in type.GetMembers(memberName))
                results.Add(m);
        }

        return results;
    }

    public IReadOnlyList<ISymbol> FindSymbols(string symbol)
    {
        // Try as type first
        var types = FindNamedTypes(symbol);
        if (types.Count > 0)
            return types.Cast<ISymbol>().ToList();

        // Try as Type.Member (methods, properties, fields, events)
        var members = FindMembers(symbol);
        if (members.Count > 0)
            return members;

        return [];
    }

    /// <summary>
    /// Depth-first enumeration of every type declared under a namespace, nested types included.
    /// Static because callers also use it on external assemblies and single compilations
    /// where no solution-wide index exists.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        return ns.GetTypeMembers().SelectMany(GetAllNestedTypes)
            .Concat(ns.GetNamespaceMembers().SelectMany(GetAllTypes));
    }

    private static IEnumerable<INamedTypeSymbol> GetAllNestedTypes(INamedTypeSymbol type)
    {
        return new[] { type }
            .Concat(type.GetTypeMembers().SelectMany(GetAllNestedTypes));
    }
}
