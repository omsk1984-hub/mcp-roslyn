# Этап 4, порция A: async-first доступ к контексту в `SolutionManager`

- Дата (UTC): 2026-10-28T19:40Z
- Режим/агент: code

## Что сделано

- Введены async-методы `EnsureReadyAsync(CancellationToken)`, `RebuildIfStaleAsync(CancellationToken)`
  и публичный `GetAnalysisContextAsync(CancellationToken)`; warmup и перестройка теперь
  **ожидаются** (`await`), а не блокируют поток вызова.
- `_rebuildGate.Wait(0)` (opportunistic acquire) заменён на `await _rebuildGate.WaitAsync(0, ct)` —
  синхронный `Wait` блокировал поток пула даже на нулевом таймауте.
- Перестройка вынесена в общий `RunRebuildUnderGateAsync(ct)`; `catch (Exception ex)` +
  `_tracker.RestoreStale(snapshot)` сохранены — перестройка по-прежнему не роняет запрос, только логирует.
- Отмена безопасна: `completed = false` + `RestoreStale` в `finally` означает, что при
  `OperationCanceledException` drained-снимок возвращается в трекер и пробрасывается наружу
  (не «выдирается и теряется»).
- Синхронный путь сохранён рабочим (51 `*Tool`, порция C), но **без** `Task.Wait()` /
  `.GetAwaiter().GetResult()`: warmup ждётся через `ManualResetEventSlim _warmupCompleted`,
  а `RebuildIfStale()` / `GetAnalysisContext()` паркуют поток на событие вокруг той же async-реализации.
  Это позволило довести `find_async_violations` по файлу до нуля, не ломая синхронных вызывающих.
- Инвариант #282 сохранён: снимок (`Loaded`, `Resolver`, `Metadata`) берётся одним `lock (_lock)`
  в обеих ветках; на тёплом пути `_resolver` не пересоздаётся.
- Тестовые швы (internal): `SetRebuildDelayForTest`, `HasStaleProjectsForTest` — позволяют
  наблюдать состояние трекера и искусственно замедлить перестройку.

## Зачем

План `plans/2026-10-28-solid-architecture-audit.md`, этап 4 (разделы 4.1/4.2): перестройка
протухшего решения больше не должна занимать поток вызова — меняется масштабируемость, не латентность.

Отклонённая альтернатива: синхронные обёртки как `EnsureReadyAsync(...).GetAwaiter().GetResult()`.
Она сохраняет работоспособность, но оставляет 2 нарушения `find_async_violations` в файле
и является ровно тем антипаттерном, который этап устраняет, — поэтому синхронный путь
переписан на событие + ту же async-реализацию вместо блокировки на Task.

## Затронутые файлы

- `src/RoslynCodeLens/SolutionManager.cs` — `EnsureReadyAsync`/`RebuildIfStaleAsync`/
  `GetAnalysisContextAsync`, `RunRebuildUnderGateAsync`, событие `_warmupCompleted`,
  синхронные `EnsureReady`/`RebuildIfStale`/`GetAnalysisContext`, тестовые швы.
- `tests/RoslynCodeLens.Tests/SolutionManagerTests.cs` — 3 новых теста (неблокирующая
  перестройка, отмена с сохранением stale-состояния, паритет sync/async).
- `plans/2026-10-28-solid-architecture-audit.md` — отметка порции A выполненной + итог.

## Проверка

- `find_async_violations`: `SolutionManager.cs` — **0** (было 2); по решению **3** —
  это сайты 3–5 порции B (`FindReferencesLogic` ×2, `FindCallersLogic` ×1), ожидаемые.
- `get_diagnostics` (RoslynCodeLens, warning): **0 errors, 0 warnings**.
- `dotnet test RoslynCodeLens.slnx`: **1252 passed, 0 failed** (база 1249 + 3 новых).
- Мутационная проверка: при замене `await DelayForTestAsync(ct)` на `Thread.Sleep(...)`
  тест `GetAnalysisContextAsync_WhileRebuilding_DoesNotBlockCallingThread` **падает**
  («returned synchronously»), т.е. тест действительно различает блокирующую и неблокирующую реализацию.

## Риски / дальше

- **Порция B**: `FindReferencesLogic.Execute` и `FindCallersLogic.Execute` → `ExecuteAsync`,
  внутренние `SymbolFinder`/`GetSemanticModelAsync` — на `await`, затем перевод 6 прод-
  и 16 тест/бенч-сайтов. Не совмещать с этой порцией.
- **Порция C**: 40+ `*Tool.Execute` → `ExecuteAsync` на `GetAnalysisContextAsync`; после неё
  синхронные `EnsureReady`/`RebuildIfStale`/`GetAnalysisContext` и событие `_warmupCompleted`
  можно удалять.
- `GetAnalysisContext()` паркует **вызывающий** поток на событие — по-прежнему блокирует,
  но не является sync-over-async. Это осознанный временный путь до порции C.
