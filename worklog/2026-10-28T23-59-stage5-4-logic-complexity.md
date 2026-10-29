# Этап 5.4: сложность двух худших методов слоя логики

- Дата (UTC): 2026-10-28T23:59Z
- Режим/агент: code (скилл `solid-plan-implementation`)

## Что сделано

`FindAsyncViolationsLogic.Execute` (CC 27 / cognitive 66) и `FindObsoleteUsageLogic.Execute`
(CC 24 / cognitive 61) разобраны на детекторы/коллекторы. Поведение не менялось, тесты-контракт
**не правились**; добавлено 10 новых тестов паритета. Два коммита, по одному на метод.

**`FindAsyncViolationsLogic` — детектор на каждый из шести шаблонов.** Новый
`src/RoslynCodeLens/Analysis/AsyncViolationDetector.cs` (namespace `Analysis`, рядом с
`SolutionScanner`, которым пользуется):

| Детектор | Шаблон |
|---|---|
| `DetectSyncOverAsyncResultInMethod` | `.Result` у `Task`/`Task<T>`/`ValueTask<T>` |
| `DetectSyncOverAsyncWaitInMethod` | `Task.Wait`/`WaitAll`/`WaitAny` |
| `DetectSyncOverAsyncGetAwaiterGetResultInMethod` | `GetAwaiter().GetResult()` + configured-варианты |
| `DetectAsyncVoidInMethod` | `async void` вне обработчика события |
| `DetectMissingAwaitInMethod` | неawait'нутый вызов в `async`-методе |
| `DetectFireAndForgetInMethod` | неawait'нутый вызов в синхронном методе |

Периодические сведения о методе собраны в `AsyncMethodScan` (declaration, symbol, semantic model,
`TaskSymbols`, project, containing name, `IsAsync`, body) — детектор принимает **один** параметр
вместо восьми. `DetectAll` владеет обходом (`SolutionScanner` + два фильтра проектов),
`DetectInMethod` — вызовом шести детекторов, `FindAsyncViolationsLogic.Execute` — сортировкой и
summary.

**`FindObsoleteUsageLogic` — обход отделён от группировки.** Новый
`src/RoslynCodeLens/Tools/ObsoleteUsageCollector.cs`: `CollectObsoleteSymbols` (сбор `[Obsolete]`
по `AttributeIndex`), `Collect` (обход production-деревьев, фильтры проекта, dedupe по
`(file, span)`, отбрасывание символов без использований), `IsWrappedByOuterRelevantNode` (правило
«один вызов — одна находка», все шесть случаев сохранены дословно), `GetCallerName`.
`FindObsoleteUsageLogic.Execute` теперь только фильтрует по `errorOnly`, строит группы и
сортирует. `ObsoleteAttributeData` стал `internal readonly record struct` с фабрикой
`FromAttribute`.

## Зачем

Оба метода были не «сложными», а **многоответственными**: async-версия выполняла шесть независимых
обнаружений в одном теле, obsolete-версия смешивала обход синтаксиса с подсчётом и сортировкой.
Любая правка одного шаблона проходила через ветвления всех остальных. После разнесения добавление
седьмого шаблона — новый метод, а не седьмая ветка `Execute`.

Почему новый тип, а не приватные методы того же класса: `FindAsyncViolationsLogic` — публичная
статическая точка входа инструмента; держать в ней 400 строк обхода дерева значит повторить
исходную проблему. `Analysis` уже содержит `SolutionScanner`, так что обход логично живёт рядом.

## Инструменты: почему не `extract_method`

План 5.4 предписывал извлечение через `get_code_actions` → `apply_code_action` → `rename_symbol`.
От этого подхода пришлось **отказаться уже на первом методе**, и вот почему:

- `extract_method` вырезает **один** связный диапазон. Здесь тело — шесть чередующихся циклов с
  общим состоянием (`semanticModel`, `taskSymbol`, `containingMethodName`, `projectName`), которые
  нужно было бы сначала переписать, чтобы получить связные диапазоны. То есть ручное
  редактирование всё равно предшествовало бы извлечению.
- Грабли 5.3 (`rename_symbol` с `renameInComments: true` переписывает XML-doc в **других** файлах
  по общим именам) делают каскад переименований шести методов в чужом файле более рискованным,
  чем сама декомпозиция.

Итог — ручное написание нового типа плюс ручное сокращение `Execute`; `rename_symbol` /
`change_signature` / `apply_code_action` **не вызывались** (на диск через MCP ничего не писалось).
Декомпозиция подтверждена метриками и тестами, а не инструментом.

## Метрика до/после

| Метрика | До | После |
|---|---|---|
| `FindAsyncViolationsLogic.Execute` — CC / cognitive | 27 / 66 | **1 / 0** |
| `FindObsoleteUsageLogic.Execute` — CC / cognitive | 24 / 61 | **2 / 1** |
| `FindAsyncViolationsLogic.cs` — LOC | 306 | **38** |
| `FindObsoleteUsageLogic.cs` — LOC | 234 | **86** |
| `AsyncViolationDetector.cs` | — | 415 (новый, ни один метод > CC 10) |
| `ObsoleteUsageCollector.cs` | — | 216 (новый, максимум CC 15 у `IsWrappedByOuterRelevantNode`) |
| Методов `src/` с CC > 15 | 22 | **19** |
| `get_diagnostics` | 0 / 0 | **0 / 0** |
| `find_disposable_misuse` | 13 | **13** (без изменений) |
| `dotnet test RoslynCodeLens.slnx` | 1286 passed | **1296 passed, 0 failed** |

## Набор находок не изменился — как это проверено

Тесты-контракт **не правились** и остались зелёными:
`FindAsyncViolationsToolTests` + `FindAsyncViolationsMissingCorelibTests` — 20/20,
`FindObsoleteUsageToolTests` — 16/16. Это проверяет: все шесть шаблонов, event-handler исключение,
linked-file dedupe (`LinkedFile_InTwoCompilations_ReportsOneViolation`), conditional access (`?.`),
qualified `new Ns.Type()`, отбрасывание нулевых использований, все три сортировки.

Плюс 10 новых тестов паритета «детектор ↔ агрегат»:
- `AsyncViolationDetectorTests` (7): для каждого шаблона `DetectOnly` == подмножество `Execute` по
  (FilePath, Line, ContainingMethod, Project); объединение шести == весь набор.
- `ObsoleteUsageCollectorTests` (3): коллектор == использования из групп `Execute`; символы без
  использований отбрасываются; фильтр проекта сужает обход, а не режет результат постфактум.

## Затронутые файлы

- `src/RoslynCodeLens/Analysis/AsyncViolationDetector.cs` — новый (+ `AsyncMethodScan`).
- `src/RoslynCodeLens/Tools/FindAsyncViolationsLogic.cs` — 306 → 38 LOC, только агрегация.
- `src/RoslynCodeLens/Tools/ObsoleteUsageCollector.cs` — новый, 216 LOC.
- `src/RoslynCodeLens/Tools/FindObsoleteUsageLogic.cs` — 234 → 86 LOC, только группировка и сорт.
- `tests/RoslynCodeLens.Tests/Tools/AsyncViolationDetectorTests.cs` — новый, 7 тестов.
- `tests/RoslynCodeLens.Tests/Tools/ObsoleteUsageCollectorTests.cs` — новый, 3 теста.
- `plans/2026-10-28-solid-architecture-audit.md` — раздел 5.4, галочки в таблице этапа.

## Проверка

- Красный → зелёный по каждому методу: CS0103 на отсутствующий тип, затем 7/7 и 3/3.
- `get_diagnostics` (severity=warning, всё решение) — **0/0** после каждого шага.
- Точечные прогоны: `FindAsyncViolations*` 20/20, `ObsoleteUsage*` 19/19, `AsyncViolationDetectorTests` 7/7.
- `get_complexity_metrics(15)` — ни `FindAsyncViolationsLogic.Execute`, ни
  `FindObsoleteUsageLogic.Execute` в списке; все новые методы ниже порога.
- `find_disposable_misuse` — 13, ни одной новой в файлах этого пункта.
- `dotnet test RoslynCodeLens.slnx` — **1296 passed, 0 failed** (8 m 14 s).

## Риски / дальше

- `IsWrappedByOuterRelevantNode` в `ObsoleteUsageCollector` — CC 15, ровно на пороге. Рекурсивное
  правило «один вызов — одна находка» не дробится без потери читаемости; следующий кандидат, если
  порог ужесточат до 12.
- `AsyncMethodScan` держит `SemanticModel` и `SyntaxNode` — это ссылки на дерево, живущее только
  на время обхода; тип не должен переживать одну итерацию `DetectAll` (не кэшируется, не хранится).
- Остаток этапа 5 после 5.4: `CheckArchitectureLogic.ExecuteCore` (22/55),
  `GetCallGraphLogic.EnumerateOutgoingCalls` (25/37), `StackTraceParser.TryParseFrame` (26/29),
  `GenerateTestSkeletonLogic` (530 LOC), `InspectExternalAssemblyLogic.ToTypeInfo` (22/23),
  `FindUncoveredSymbolsLogic.EnumerateCandidates` (17/57) — следующие заходы, не трогались.
