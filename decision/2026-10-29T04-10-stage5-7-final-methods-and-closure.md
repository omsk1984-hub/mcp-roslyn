# Пункт 5.7 этапа 5: финальная группа из 11 методов + закрытие этапа 5

- Дата (UTC): 2026-10-29T04:10Z
- Режим/агент: code

## Что сделано
- Декомпозированы **11 методов** с CC ≥ 15 (последняя группа этапа 5), извлечение
  **по ответственностям** вручную (`apply_diff`), Roslyn-рефакторинги не вызывались:
  - `CognitiveComplexityCalculator.Walker.Visit` (CC 21 → 4): `IsNestingWithScore` /
    `IsNestingWithoutScore` / `ScoreFlatTerms`. Наборы кейсов и правила вложенности
    сохранены **по построению** — значения на тех же входах совпадают.
  - `ComplexityCalculator.Calculate` (CC 16 → 1): `1 + CountDecisionNodes +
    CountShortCircuitTokens`. Семантика расчёта не изменена.
  - `ReferenceClassifier.ClassifyTypeContext` (CC 15 → 4): `switch`-выражение
    `ContextKindOf` (12 веток). **Остался синхронным и чистым** (контракт kinds
    `find_references`, журнал 4B).
  - `ChangeSignatureBridge.Members.Resolve` (CC 21 → 3): `Types` record + 4 резолвера.
  - `DeadCodeFilters.Classify` (CC 17 → 8): лестница приоритетов из 4 предикатов.
  - `FindUnusedSymbolsLogic.ShouldSkipMember` (CC 16 → 4, cognitive 24 → 1): 4 предиката.
  - `GetTestSummaryLogic.IsExcludedNamespace` (CC 16 → 2): таблица `ExcludedNamespaceRoots`
    + `IsRootOrDeeper`.
  - `FindGodObjectsLogic.Execute` (CC 15 → 2, cognitive 22 → 1): `Thresholds` record +
    3 прохода + помощники.
  - `ApplyFixLogic.ExecuteAsync` (CC 16 → 3): 6 фазовых методов.
  - `ExtractMethodLogic.ExecuteAsync` (CC 16 → 5): 6 фазовых методов.
  - `ObsoleteUsageCollector.IsWrappedByOuterRelevantNode` (CC 15 → 5):
    `IsDirectlyWrappedByOuterNode` `switch`-выражение.
- **Закрыт критерий сложности этапа 5**: `get_complexity_metrics(threshold=15)` —
  **0 методов** (было 11); максимальный CC в решении — **14**.
- **Закрыт этап 5 с явно перечисленным остатком**: критерий крупных типов
  (`find_large_classes(300,15)` — 0 типов в `src/`) **не закрыт** — остаётся **18 типов**
  > 300 LOC. По решению они **не дробятся механически** (это декомпозиция типа, а не
  метода, отдельный заход). Остаток зафиксирован в плане (раздел 5.7) с обоснованием.

## Зачем
- Завершить этап 5 плана `plans/2026-10-28-solid-architecture-audit.md`: свести все
  методы под порог CC 15 и закрыть критерии готовности.
- Три метода контракт-критичны (пороги аудита построены на `ComplexityCalculator` /
  `CognitiveComplexityCalculator`; `ReferenceClassifier` — контракт kinds `find_references`):
  их семантика **не менялась**, неизменность доказана зелёными тестами-контракт **без правок**.
- Остаток крупных типов не дробился механически: механическое дробление 18 типов в одном
  заходе дало бы огромный diff без изменения поведения и не было бы покрыто отдельными
  тестами; это отдельный заход (декомпозиция типа).

## Затронутые файлы
- `src/RoslynCodeLens/Analysis/CognitiveComplexityCalculator.cs` — `Visit` → 3 помощника
- `src/RoslynCodeLens/Analysis/ComplexityCalculator.cs` — `Calculate` → 2 счётчика
- `src/RoslynCodeLens/Analysis/ReferenceClassifier.cs` — `ClassifyTypeContext` → `ContextKindOf`
- `src/RoslynCodeLens/Analysis/ChangeSignatureBridge.cs` — `Members.Resolve` → `Types` + 4 резолвера
- `src/RoslynCodeLens/Tools/DeadCodeFilters.cs` — `Classify` → 4 предиката
- `src/RoslynCodeLens/Tools/FindUnusedSymbolsLogic.cs` — `ShouldSkipMember` → 4 предиката
- `src/RoslynCodeLens/Tools/GetTestSummaryLogic.cs` — `IsExcludedNamespace` → таблица + `IsRootOrDeeper`
- `src/RoslynCodeLens/Tools/FindGodObjectsLogic.cs` — `Execute` → `Thresholds` + 3 прохода
- `src/RoslynCodeLens/Tools/ApplyFixLogic.cs` — `ExecuteAsync` → 6 фаз
- `src/RoslynCodeLens/Tools/ExtractMethodLogic.cs` — `ExecuteAsync` → 6 фаз
- `src/RoslynCodeLens/Tools/ObsoleteUsageCollector.cs` — `IsWrappedByOuterRelevantNode` → `IsDirectlyWrappedByOuterNode`
- `plans/2026-10-28-solid-architecture-audit.md` — раздел 5.7 + статус этапа 5 (с остатком)

## Проверка
- get_diagnostics: **0 ошибок / 0 предупреждений**
- get_complexity_metrics(threshold=15): **0 методов** (было 11); max CC в решении **14**
- dotnet test RoslynCodeLens.slnx: **1296 passed, 0 failed, 0 skipped** (8 m 32 s)
- Тесты-контракт зелёные **без единой правки**: `CognitiveComplexityCalculatorTests` 18/18,
  `ComplexityCalculatorTests` 28/28, `ReferenceClassifierTests`, `ChangeSignatureBridgeTests`,
  `DeadCodeFiltersTests`, `FindUnusedSymbolsToolTests`, `GetTestSummaryToolTests`,
  `FindGodObjectsToolTests`, `ApplyFixLogicTests`, `ExtractMethodLogicTests`,
  `FindObsoleteUsageToolTests`.
- find_circular_dependencies: **0**; check_architecture (3 forbid-правила): **0 нарушений**

## Риски / дальше
- **Остаток этапа 5 (явно перечислен в плане 5.7):** 18 типов > 300 LOC в `src/`
  (худший — `SolutionLoader` 670/19) + декомпозиция `GenerateTestSkeletonLogic`
  (530/18, строка 541 таблицы этапа 5). Это **декомпозиция типов**, отдельный заход.
- Критерий сложности (CC ≥ 15) закрыт полностью; критерий крупных типов — с остатком.
- Изменяющие MCP-инструменты не применялись (всё — `apply_diff` вручную);
  `rename_symbol` / `change_signature` не вызывались.
