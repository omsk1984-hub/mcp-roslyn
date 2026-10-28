# Этап 4, порция B: async-first в `FindReferencesLogic` и `FindCallersLogic`

- Дата (UTC): 2026-10-28T20:03Z
- Режим/агент: code

## Что сделано

- `FindReferencesLogic.Execute` → `ExecuteAsync(..., CancellationToken)`; `FindCallersLogic.Execute` →
  `ExecuteAsync(..., CancellationToken)`. Синхронные версии **удалены целиком** (не оставлены рядом),
  после того как `find_references` по ним дал 0.
- `SymbolFinder.FindReferencesAsync` / `FindCallersAsync` и `Document.GetSemanticModelAsync`
  ожидаются через `await ... .ConfigureAwait(false)` вместо `.GetAwaiter().GetResult()`.
- `ct.ThrowIfCancellationRequested()` в начале `ExecuteAsync` и перед каждым `SymbolFinder`-вызовом —
  pre-cancelled токен даёт `OperationCanceledException`, а не «зависание».
- Каскад: `AnalyzeChangeImpactLogic.ExecuteAsync`, `AnalyzeMethodLogic.ExecuteAsync`,
  `AnalyzeChangeImpactTool.ExecuteAsync` / `AnalyzeMethodTool.ExecuteAsync` (→ `Task<T>`),
  `FindReferencesTool.ExecuteAsync` / `FindCallersTool.ExecuteAsync` (→ `Task<ToolListResult<T>>`, как
  уже давно делает `GetCallGraphTool`). `ValidateEditLogic` уже был `async` — переведён только
  внутренний вызов на `FindReferencesLogic.ExecuteAsync`.
- 20 тест-сайтов → `async Task`; `IncrementalRebuildTests` — хелперы `ReferencesAsync` /
  `CountReferencesAsync` / `CountCallersAsync`, reader-цикл `Task.Run(async …)`;
  4 бенч-метода `CodeGraphBenchmarks` → `Task<T>` (+ `using RoslynCodeLens.Models`).

## Зачем

План `plans/2026-10-28-solid-architecture-audit.md`, этап 4, порция B (сайты 3–5): после порции A
оставались ровно три `SyncOverAsyncGetAwaiterGetResult` в слое логики. Обход всего решения
`SymbolFinder`-ом занимал поток пула на всё время сканирования; цель — масштабируемость, не
латентность.

Ключевое решение — **`ReferenceClassifier` не тронут**. Его API `Classify(node, symbol,
Func<SemanticModel?>)` синхронный и чистый, тесты опираются на это. Вместо перевода классификатора
в async модель ожидается **заранее только для узлов с родителем `ArgumentSyntax`** — единственной
ветки, которой нужна семантика (резолв невыраженного `out`/`ref`, COM-interop), а затем передаётся
в `Classify` уже готовой через `() => model`. Ленивость сохранена: на обычном сканировании
semantic model по-прежнему не строится.

Отклонённые альтернативы: (1) оставить `Execute` как синхронную обёртку над `ExecuteAsync` — снова
`GetAwaiter().GetResult()`, то есть ровно тот антипаттерн, который этап устраняет; (2) сделать
`Classify` async — публичный API чистого классификатора и его тесты менялись бы ради редкой ветки.
**(2) отклонено по принципу минимальной правки.**

## Затронутые файлы

- `src/RoslynCodeLens/Tools/FindReferencesLogic.cs` — `ExecuteAsync` + `ScanForReferencesAsync` +
  `GetModelAsync(ct)`; `GetModel` заменён.
- `src/RoslynCodeLens/Tools/FindCallersLogic.cs` — `ExecuteAsync` с `await FindCallersAsync(…, ct)`.
- `src/RoslynCodeLens/Tools/AnalyzeChangeImpactLogic.cs`, `AnalyzeMethodLogic.cs` — `ExecuteAsync`.
- `src/RoslynCodeLens/Tools/AnalyzeChangeImpactTool.cs`, `AnalyzeMethodTool.cs`,
  `FindReferencesTool.cs`, `FindCallersTool.cs` — `Task<T>`-возврат и `CancellationToken`.
- `src/RoslynCodeLens/Tools/ValidateEditLogic.cs` — внутренний вызов → `await …ExecuteAsync(…, ct)`.
- `tests/RoslynCodeLens.Tests/FindReferencesLogicTests.cs` — хелпер `FindAsync` + 2 новых теста.
- `tests/RoslynCodeLens.Tests/Tools/{FindReferencesToolTests,FindCallersToolTests,AnalyzeChangeImpactToolTests,AnalyzeMethodToolTests,RazorGeneratedCodeTests}.cs`
  — `async Task`.
- `tests/RoslynCodeLens.Tests/IncrementalRebuildTests.cs` — `*Async`-хелперы и `Task.Run(async …)`.
- `benchmarks/RoslynCodeLens.Benchmarks/CodeGraphBenchmarks.cs` — 4 бенч-метода → `Task<T>`, `using`.
- `plans/2026-10-28-solid-architecture-audit.md` — порция B отмечена выполненной + итог.

## Проверка

- `find_async_violations` по решению: **0** (было 3).
- `get_diagnostics` (все проекты, severity=warning): **0 errors, 0 warnings**.
- `find_references` по `FindReferencesLogic.Execute` / `FindCallersLogic.Execute`: **0**;
  по `FindReferencesLogic.ExecuteAsync`: **16** вызовов (3 прод, 1 бенч, 12 тестов).
- `dotnet test RoslynCodeLens.slnx`: **1254 passed, 0 failed** (база 1252 + 2 новых).
- Красные тесты зафиксированы до правки: файл не компилировался (`ExecuteAsync` отсутствует) —
  это и был RED-этап; после реализации оба новых теста зелёные, точечный прогон 32/32.

## Риски / дальше

- **Порция C** (`40+ *Tool.Execute` → `ExecuteAsync` на `GetAnalysisContextAsync`) не начата — как
  и требовалось. После неё можно удалять синхронные `EnsureReady` / `RebuildIfStale` /
  `GetAnalysisContext` и событие `_warmupCompleted` в `SolutionManager`.
- `FindReferencesTool`/`FindCallersTool` теперь отдают `Task<ToolListResult<T>>` — MCP-хост это
  поддерживает (`GetCallGraphTool` служит доказательством), но фактическая проверка транспорта
  запланирована в порции C.
- Ленивость semantic model сохранена эвристикой «родитель — `ArgumentSyntax`». Если Roslyn начнёт
  спрашивать модель в другой ветке, `Classify` вернёт то же значение, что и при `null`-модели
  (`RefKind.None` → `read`), — деградация безопасная, но невидимая; при появлении таких случаев
  пересмотреть, а не добавлять синхронный fallback.
