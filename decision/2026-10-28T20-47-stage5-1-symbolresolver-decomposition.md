# Этап 5.1: декомпозиция god-объекта `SymbolResolver`

- Дата (UTC): 2026-10-28T20:47Z
- Режим/агент: code (скилл `solid-plan-implementation`)

## Что сделано

- `SymbolResolver` разбит на три узких индекса плюс фасад:
  - `Symbols/SymbolIndex.cs` — поиск по имени: `AllTypes`, `FindNamedTypes`, `FindMethods`,
    `FindEvents`, `FindMembers`, `FindSymbols`, словари `TypesBySimpleName` /
    `MembersBySimpleName` / `AttributeIndex`, статический `GetAllTypes(INamespaceSymbol)`;
  - `Symbols/TypeIndex.cs` — иерархии: `GetInterfaceImplementors`, `GetDerivedTypes`,
    карты `_interfaceImplementors` / `_derivedTypes`, плюс `FindNamedTypes` как делегат;
  - `Symbols/LocationIndex.cs` — местоположение: `GetLocation`, `GetFileAndLine`,
    `GetProjectName` (обе перегрузки), `GetMarkupSource`, `IsGenerated`, словари путей.
- `SymbolResolver` стал **фасадом** (414 → 71 LOC) с тремя точками доступа к индексам
  (`Symbols`, `Types`, `Locations`) и полным сохранением прежней публичной поверхности.
- Один обход типов на все индексы: `TypeIndex(loaded, symbolIndex)` принимает уже
  построенный `SymbolIndex`, поэтому стоимость конструирования не выросла.
- Удалены два мёртвых члена, появившихся при переносе: делегирование
  `SymbolResolver.FindMembers` (единственный прежний вызов был внутренним, из `FindSymbols`)
  и свойство `TypeIndex.Symbols` (`find_references` = 0 для обоих).
- Новый тест `tests/RoslynCodeLens.Tests/SymbolIndexDecompositionTests.cs` (5 тестов):
  проверяет раскладку обязанностей по индексам и паритет ответов фасада.

## Зачем

Единственный god-объект решения: 414 LOC, 45 членов, 13 полей —
`find_god_objects` помечал его по 3 осям размера и 1 оси зацепления.

**Форма выбрана — фасад, а не замена тремя типами.** `find_references` по `SymbolResolver`
дал **218 ссылок в 3 проектах** (124 в prod, 92 в тестах, 2 в бенчмарках), включая 25
`object_creation`. Замена типа потребовала бы правки ~200 мест и переписала бы
позиционные параметры десятков `*Logic` — несоразмерный риск для чистого переноса кода.
Фасад даёт новому коду точки входа в нужный индекс и остаётся удаляемым, когда
вызывающих переведут на индексы напрямую.

**Все члены сохранены.** Инвентаризация `find_references` по каждому публичному члену
показала ненулевое число потребителей у всех 21 публичных членов. Отдельно отмечен
`GetProjectName(ProjectId)`: `find_references` по сигнатуре с `ProjectId` даёт 0, но
перегрузка активно используется (34 вызова на перегрузку) — она разрешается компилятором
на конкретный `ProjectId` на 16 сайтах (`FindObsoleteUsageLogic`, `GetDiagnosticsLogic`,
`GetTestSummaryLogic`, `GetPublicApiSurfaceLogic`, `FindEventSubscribersLogic`,
`FindTestsForSymbolLogic`, `FindUncoveredSymbolsLogic`, `GetGeneratedCodeLogic`,
`GetFileOverviewLogic`, `ValidateEditLogic`, `SolutionScanner`, `FindCallersLogic`,
`FindReferencesLogic`, `GetInstantiationOptionsLogic`, `FindUncoveredSymbolsLogic`).
Поэтому перегрузка не удалялась — от удаления «членов без потребителей» пришлось отказаться
полностью.

## Ключевое уточнение к аудиту

⚠️ **`find_god_objects` возвращал пусто и ДО разбиения.** Аудит утверждал, что
`SymbolResolver` — единственная находка. Фактически порог `minOutgoingNamespaces = 5`
не достигался: `SymbolResolver` зависел всего от 3 не-BCL namespace
(`System.Runtime.InteropServices`, `Microsoft.CodeAnalysis`, `RoslynCodeLens.Symbols`).
Причина, по которой это важно: критерий готовности «`find_god_objects` — пусто» **не мог
провалиться** и поэтому ничего не доказывал. Реальное подтверждение декомпозиции даёт
динамика `find_large_classes` и LOC — они и зафиксированы ниже.

## Метрика до/после

| Метрика | До | После |
|---|---|---|
| `SymbolResolver` — LOC | **414** | **71** |
| `SymbolResolver` — членов | **45** | 31 |
| `SymbolResolver` — полей | **13** | **3** |
| `SymbolResolver` в `find_large_classes(maxLines=300, maxMembers=15)` | **да** (414 строк) | **нет** (71 строка) |
| `find_god_objects` | пусто | пусто |
| `get_diagnostics` | 0 / 0 | **0 / 0** |
| `dotnet test RoslynCodeLens.slnx` | 1254 passed | **1259 passed, 0 failed** |

Новые типы (ни один не превышает пороги `< 300 LOC`, `< 15 полей`):

| Тип | LOC | Члены | Поля |
|---|---|---|---|
| `SymbolIndex` | 254 | 27 | 6 |
| `TypeIndex` | ~90 | 4 | 3 |
| `LocationIndex` | ~180 | 7 | 4 |

`SymbolIndex` (254 LOC) и фасад (31 член) попадают в `find_large_classes` при
порогах `maxMembers=15`, но **не по строкам** — `SymbolIndex` ниже порога 300 строк.
Членство в `SymbolIndex` — это в основном публичные точки входа поиска, а не поля
состояния (6 полей), то есть метрика по членам здесь отражает ширину контракта, а не
god-объектность.

## Затронутые файлы

- `src/RoslynCodeLens/Symbols/SymbolIndex.cs` — новый тип, поиск по имени.
- `src/RoslynCodeLens/Symbols/TypeIndex.cs` — новый тип, иерархии и реализации.
- `src/RoslynCodeLens/Symbols/LocationIndex.cs` — новый тип, файл/строка/проект.
- `src/RoslynCodeLens/SymbolResolver.cs` — переписан как фасад (414 → 71 LOC).
- `tests/RoslynCodeLens.Tests/SymbolIndexDecompositionTests.cs` — новые тесты (5).
- `plans/2026-10-28-solid-architecture-audit.md` — пункт 5.1 отмечен, добавлен раздел
  с метрикой до/после.

## Проверка

- Красный тест: `SymbolIndexDecompositionTests` не компилировался (CS0246 на
  `SymbolIndex`/`TypeIndex`/`LocationIndex`, CS1061 на `SymbolResolver.Symbols`/`Types`/`Locations`).
- Зелёный: `SymbolIndexDecompositionTests` — **5/5**.
- `SymbolResolverTests` — зелёные без правок (11 тестов, фиксируют прежнее поведение).
- `get_diagnostics` (severity=warning, весь солюшен) — **0 errors, 0 warnings**.
- `dotnet test RoslynCodeLens.slnx` — **1259 passed, 0 failed** (база 1254 + 5 новых).
- `find_god_objects` — пусто; `find_large_classes(300, 15)` — `SymbolResolver` только
  по членам (71 строка), `SymbolIndex` 254 строки (< 300).
- `find_unused_symbols` — 126 (было 124 в аудите: +2 моих кандидата найдены и удалены,
  счётчик вернулся к базовому уровню после чистки).

## Риски / дальше

- Публичная поверхность `SymbolResolver` **расширена** тремя свойствами (`Symbols`,
  `Types`, `Locations`); остальные 21 член без изменений. Это аддитивное изменение —
  существующие вызывающие не затронуты.
- `SymbolResolver` остаётся точкой входа для 218 ссылок. Следующий шаг — перевести
  вызывающих на конкретный индекс по месту использования и удалить фасад; это отдельный
  заход, не входивший в текущий объём.
- Остальные 7 пунктов этапа 5 (`FindAsyncViolationsLogic`, `FindObsoleteUsageLogic`,
  `SolutionLoader.OpenPerProjectAsync`, `CheckArchitectureLogic`, `GenerateTestSkeletonLogic`,
  `GetCallGraphLogic`, `StackTraceParser`) **не тронуты** — выполняются отдельными заходами.
- Наблюдение вне объёма: `find_god_objects` не помечает ни одного типа в решении,
  хотя `SolutionManager` — 899 LOC / 61 член. Порог outgoing-namespace фактически
  недостижим для этого кода; критерий готовности этапа 5 по `find_god_objects` стоит
  перепроверить до объявления этапа завершённым.
