using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Фиксирует декомпозицию god-объекта <see cref="SymbolResolver"/> (этап 5.1 плана
/// <c>plans/2026-10-28-solid-architecture-audit.md</c>).
///
/// Дефект: 414 LOC / 45 членов / 13 полей в одном типе, три несвязанные обязанности:
/// поиск по имени, иерархии типов и вычисление местоположения (файл/строка/проект).
///
/// Тест держит **два** инварианта:
/// 1. обязанности действительно разъехались по трём отдельным типам, и каждый отдаёт
///    ровно те ответы, что раньше отдавал <c>SymbolResolver</c> (паритет поведения);
/// 2. публичный фасад <see cref="SymbolResolver"/> продолжает отвечать так же —
///    иначе 218 вызывающих изменили бы поведение.
/// </summary>
public class SymbolIndexDecompositionTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async ValueTask InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
    }

    public ValueTask DisposeAsync() => default;

    [Fact]
    public void SymbolIndex_FindsTypesAndMembersByName()
    {
        var symbolIndex = new SymbolIndex(_loaded);

        Assert.Contains(symbolIndex.FindNamedTypes("Greeter"), t => string.Equals(t.Name, "Greeter"));
        Assert.NotEmpty(symbolIndex.FindMethods("Greeter.Greet"));
        Assert.NotEmpty(symbolIndex.FindSymbols("Greeter.Greet"));
        Assert.NotEmpty(symbolIndex.AllTypes);
    }

    [Fact]
    public void SymbolIndex_KeepsNameLookupDictionaries()
    {
        var symbolIndex = new SymbolIndex(_loaded);

        Assert.NotEmpty(symbolIndex.TypesBySimpleName);
        Assert.NotEmpty(symbolIndex.MembersBySimpleName);
        Assert.NotEmpty(symbolIndex.AttributeIndex);
    }

    [Fact]
    public void TypeIndex_AnswersHierarchyQueries()
    {
        var typeIndex = new TypeIndex(_loaded);

        // TypeIndex не индексирует типы сам — он берёт их из SymbolIndex того же снимка.
        var viaSymbolIndex = new SymbolIndex(_loaded).FindNamedTypes("TestLib.Greeter");
        var viaTypeIndex = typeIndex.FindNamedTypes("TestLib.Greeter");
        Assert.Equal(viaSymbolIndex.Count, viaTypeIndex.Count);

        var greeter = Assert.Single(viaTypeIndex);

        // Фикстура объявляет `FancyGreeter : Greeter` — производный тип должен быть найден.
        Assert.Contains(typeIndex.GetDerivedTypes(greeter), t => string.Equals(t.Name, "FancyGreeter"));

        // `Greeter : IGreeter`, плюс CrossProjectGreeter реализует тот же интерфейс
        // в другом проекте — проверяем, что карта реализаций заполнена и кросс-проектно.
        var greeterInterface = Assert.Single(typeIndex.FindNamedTypes("IGreeter"));
        var implementors = typeIndex.GetInterfaceImplementors(greeterInterface);
        Assert.Contains(implementors, t => string.Equals(t.Name, "Greeter"));
        Assert.Contains(implementors, t => string.Equals(t.Name, "CrossProjectGreeter"));

        // Запрос по символу, которого нет в карте, отдаёт пустой список, а не null/исключение.
        Assert.Empty(typeIndex.GetDerivedTypes(greeterInterface));
    }

    [Fact]
    public void LocationIndex_AnswersFileLineProjectAndGeneratedQueries()
    {
        var locationIndex = new LocationIndex(_loaded);

        // LocationIndex намеренно не ищет по имени — тип приходит снаружи. Это и есть
        // разделение обязанностей, которое проверяет декомпозиция.
        var symbolIndex = new SymbolIndex(_loaded);
        var type = Assert.Single(symbolIndex.FindNamedTypes("TestLib.Greeter"));

        var (file, line) = locationIndex.GetFileAndLine(type);
        Assert.NotEmpty(file);
        Assert.True(line > 0);

        // GetProjectName(ISymbol) resolves through the file→project map, so a source type
        // must report a non-empty project and the same name as its own project id.
        var projectName = locationIndex.GetProjectName(type);
        Assert.False(string.IsNullOrEmpty(projectName));
        var owningProject = _loaded.Solution.Projects.Single(p => string.Equals(p.Name, projectName));
        Assert.Equal(projectName, locationIndex.GetProjectName(owningProject.Id));

        Assert.False(locationIndex.IsGenerated(file));
        Assert.Null(locationIndex.GetMarkupSource(file));
        Assert.True(locationIndex.IsGenerated(@"C:\project\obj\Debug\net10.0\Generated.cs"));
        Assert.True(locationIndex.IsGenerated(null));
    }

    /// <summary>
    /// Фасад обязан отвечать идентично индексам: разбиение — перенос кода, не смена поведения.
    /// </summary>
    [Fact]
    public void Facade_DelegatesToIndexes_WithoutChangingAnswers()
    {
        var resolver = new SymbolResolver(_loaded);

        Assert.IsType<SymbolIndex>(resolver.Symbols, exactMatch: false);
        Assert.IsType<TypeIndex>(resolver.Types, exactMatch: false);
        Assert.IsType<LocationIndex>(resolver.Locations, exactMatch: false);

        var viaFacade = resolver.FindNamedTypes("TestLib.Greeter");
        var viaIndex = resolver.Symbols.FindNamedTypes("TestLib.Greeter");

        // Делегирование — не копия: фасад отдаёт те же экземпляры символов, что индекс.
        Assert.Equal(viaIndex.Count, viaFacade.Count);
        Assert.All(viaFacade, t => Assert.Contains(t, viaIndex, SymbolEqualityComparer.Default));

        Assert.Equal(resolver.Symbols.FindMethods("Greeter.Greet").Count, resolver.FindMethods("Greeter.Greet").Count);
        Assert.Equal(resolver.Symbols.FindSymbols("Greeter.Greet").Count, resolver.FindSymbols("Greeter.Greet").Count);
        Assert.Equal(resolver.Symbols.AllTypes.Count, resolver.AllTypes.Count);

        var type = Assert.Single(viaFacade);
        var file = type.Locations[0].SourceTree!.FilePath;
        Assert.Equal(resolver.Locations.IsGenerated(file), resolver.IsGenerated(file));
        Assert.Equal(resolver.Locations.GetMarkupSource(file), resolver.GetMarkupSource(file));
        Assert.Equal(resolver.Locations.GetProjectName(type), resolver.GetProjectName(type));
        Assert.Equal(resolver.Types.GetDerivedTypes(type), resolver.GetDerivedTypes(type));
    }
}
