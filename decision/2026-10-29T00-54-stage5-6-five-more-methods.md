# Этап 5.6 — пять следующих методов по сложности

- Дата (UTC): 2026-10-29T00:54Z
- Режим/агент: code

## Что сделано

Декомпозированы пять методов из рейтинга `get_complexity_metrics`, поведение не менялось.
Начато с самого тяжёлого по cognitive во всём решении — `EnumerateCandidates` (57).

| Метод | CC до → после | Cognitive до → после | Вложенность до |
|---|---|---|---|
| `FindUncoveredSymbolsLogic.EnumerateCandidates` | 17 → **2** | **57** → **3** | 5 |
| `InspectExternalAssemblyLogic.ToTypeInfo` | **22** → **1** | 23 → **1** | 3 |
| `ReferenceClassifier.ClassifyValueContext` | **21** → **3** | 18 → **3** | 2 |
| `GetExtensionMethodsLogic.ResolveSyntax` | 19 → **1** | 24 → **2** | 3 |
| `ProjectClosure.Compute` | 17 → **1** | 28 → **1** | 3 |

Каждый метод разнесён **по ответственностям** (не по строкам):

- **`EnumerateCandidates`** — по этапам пайплайна: `CollectCandidatesFromCompilation`
  (5/8), `IsEligibleType` (2/2), `ClassifyCandidateMember` (8/8), `IsCandidateMethod`
  (2/1), `IsCandidateProperty` (4/4). Порядок трёх общих фильтров сохранён дословно —
  именно он отсекает неявные accessor'ы авто-свойств.
- **`ToTypeInfo`** — по фасетам DTO: `TypeKindName`, `ReadModifiers`, `ReadBaseType`,
  `ReadMembers` (5/9), `ReadAttributes`, `EnumerateMemberSources`, `IsVisibleMember`
  (5/4). `ToTypeInfo` стал выражением-композицией.
- **`ClassifyValueContext`** — критичный контракт `find_references`; **сигнатура и
  синхронность не тронуты** (см. журнал 4B: классификатор сознательно оставлен
  синхронным и чистым). Извлечены `ClimbToEffectiveExpression` (6/3),
  `IsInvocationTarget` (3), `ClassifyByParent` (9/3), `ClassifyArgument` (5/3),
  `IsIncrementOrDecrement`.
- **`ResolveSyntax`** — `switch`-выражение плюс `ResolveArray`, `ResolveNullable`,
  `ResolveTuple` (6/7), `ResolvePredefined`. Ветви взаимоисключащи по виду синтаксиса.
- **`ProjectClosure.Compute`** — `ToOrdinalSet`, `CollectSeeds` (6/8),
  `CompileIncludeGlobs` (3/3), `ThrowIfNoSeeds`, `WalkClosure` (6/11).

## Зачем

Пункт 5.6 плана: продолжение снижения сложности наиболее тяжёлых методов. Приоритет
`EnumerateCandidates` продиктован показателем cognitive 57 при CC всего 17 — метод был
тяжелочитаем из-за пятиуровневой вложенности, а не из-за числа ветвей, поэтому разбиение
по этапам пайплайна дало максимальный выигрыш (57 → 3).

**Отклонённые альтернативы:**

- `extract_method` (Roslyn) вместо ручного `apply_diff` — вырезает один связный диапазон
  строк; здесь требовалась предварительная переформулировка тела в именованную цепочку
  (`ToTypeInfo` и `ResolveSyntax` вообще превратились в `switch`/композицию), то есть шаг
  не совпадает с диапазоном.
- `rename_symbol` для новых имён — каскад по общим именам (`Read`, `Resolve`) рискованнее
  самой правки; новые методы добавлялись с уже финальными именами.
- Новые паритетные тесты — не нужны: пункт чистый перенос, а неизменность набора находок
  и видов ссылок и есть требуемое доказательство (как в 5.5).

**Сознательно не тронуто:** `ReferenceClassifier.ClassifyTypeContext` (CC 15) — соседний
метод в том же файле, но вне списка пункта; он ровно на пороге, декомпозиция не требуется.
Также не тронуты остальные 8 методов с CC ≥ 16 и типы `GenerateTestSkeletonLogic` /
`ChangeSignatureBridge` — они в следующих заходах.

## Затронутые файлы

- `src/RoslynCodeLens/Tools/FindUncoveredSymbolsLogic.cs` — `EnumerateCandidates` разложен
  на 5 методов (обход → фильтр типа → фильтр члена → классификация).
- `src/RoslynCodeLens/Tools/InspectExternalAssemblyLogic.cs` — `ToTypeInfo` стал
  композицией; добавлены 7 ридеров фасетов; снят внутренний `#pragma` (перенесён в
  `ReadAttributes`) и убрана ручная сборка списка членов из тела.
- `src/RoslynCodeLens/Analysis/ReferenceClassifier.cs` — `ClassifyValueContext` разнесён
  на 5 шагов; публичный `Classify` и `Func<SemanticModel?>` не менялись, метод синхронный.
- `src/RoslynCodeLens/Tools/GetExtensionMethodsLogic.cs` — `ResolveSyntax` стал
  `switch`-выражением; добавлены `ResolveArray`, `ResolveNullable`, `ResolveTuple`,
  `ResolvePredefined`.
- `src/RoslynCodeLens/ProjectClosure.cs` — `Compute` разложен на `ToOrdinalSet`,
  `CollectSeeds`, `CompileIncludeGlobs`, `ThrowIfNoSeeds`, `WalkClosure`.
- `plans/2026-10-28-solid-architecture-audit.md` — добавлен подраздел 5.6, обновлён
  «Остаток этапа 5».

## Проверка

- `get_diagnostics` (проект `RoslynCodeLens`, severity warning): **0 errors, 0 warnings**
  до и после. `validate_edit` в этой сборке сервера отсутствует — использован
  документированный fallback `get_diagnostics` с `filePath`.
- `get_complexity_metrics(threshold=15)`: пять целевых методов **отсутствуют**; ни один
  новый метод не превышает **CC 9 / cognitive 11**. Максимум по решению снизился
  с 57 до 37 (`FindNamespaceCycles`, вне пункта).
- `find_references`: `ReferenceClassifier.Classify` — 4 вызова (1 prod + 3 теста),
  `ProjectClosure.Compute` — 9 вызовов (1 prod + 8 тестов), сигнатуры не изменились.
- Точечно: `FindUncoveredSymbolsToolTests` — 11/11 после первой правки.
- Шесть наборов-контрактов: **91/91** зелёные **без правок тестов** —
  `FindUncoveredSymbolsToolTests`, `InspectExternalAssemblyToolTests`,
  `ReferenceClassifierTests`, `GetExtensionMethodsLogicTests`,
  `GetExtensionMethodsFixtureTests`, `ProjectClosureTests`.
- Полный прогон `dotnet test RoslynCodeLens.slnx`: **1296 passed, 0 failed, 0 skipped**
  (8 m 20 s) — база не снизилась.

## Риски / дальше

- Дальше по этапу 5: `ChangeSignatureBridge.Members.Resolve` (21),
  `CognitiveComplexityCalculator.Walker.Visit` (21), `DeadCodeFilters.Classify` (17),
  `ComplexityCalculator.Calculate` (16), `ApplyFixLogic.ExecuteAsync` (16),
  `ExtractMethodLogic.ExecuteAsync` (16), `FindUnusedSymbolsLogic.ShouldSkipMember` (16),
  `GetTestSummaryLogic.IsExcludedNamespace` (16); на пороге 15 —
  `ReferenceClassifier.ClassifyTypeContext`, `FindGodObjectsLogic.Execute`,
  `ObsoleteUsageCollector.IsWrappedByOuterRelevantNode`.
- `GenerateTestSkeletonLogic` (530 LOC / 18 членов) остаётся — это декомпозиция типа,
  отдельный заход.
- `ReferenceClassifier` по-прежнему чистый и синхронный; при любой будущей правке
  классификации обязательны `ReferenceClassifierTests` без изменений и ручная сверка
  видов ссылок — на них построен `find_references`.
