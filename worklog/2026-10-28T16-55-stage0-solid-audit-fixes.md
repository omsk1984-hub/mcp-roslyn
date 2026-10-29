# Этап 0 плана SOLID-аудита: неотложные дефекты

- Дата (UTC): 2026-10-28T16:55Z
- Режим/агент: code

## Что сделано

Пять пунктов этапа 0, каждый отдельным коммитом (цикл «красный тест → правка →
`get_diagnostics` → точечные тесты → коммит»).

- **0.1** `Workspace.WorkspaceFailed` → `RegisterWorkspaceFailedHandler()` в
  [`SolutionLoader.cs`](src/RoslynCodeLens/SolutionLoader.cs) (2 сайта) — убраны 2× CS0618.
- **0.1b** Возвращаемый подпиской `IDisposable` обёрнут в `using` — иначе инструмент
  выдавал 2× `DisposableDiscarded` (регрессия, пойманная на верификации).
- **0.2** CS8619: лямбды таймаут-обёрток типизированы как `Task<Solution?>`/`Task<Project?>`.
- **0.3** CS8602 в [`ValidateEditLogic.cs`](src/RoslynCodeLens/Tools/ValidateEditLogic.cs):
  чтение `newErrors.Count` привязано к проверке baseline (`newErrors is { Count: 0 }`).
- **0.4** Контракт владения `PEFile`: **кэш владеет объектом** (`Get` кладёт инстанс в
  словарь, `Invalidate`/`Dispose` его освобождают), поэтому `using` на call-site —
  use-after-dispose. Задокументировано в
  [`IlDisassemblerAdapter.cs`](src/RoslynCodeLens/Metadata/IlDisassemblerAdapter.cs) и
  [`PeekIlLogic.cs`](src/RoslynCodeLens/Tools/PeekIlLogic.cs), контракт закреплён тестами.
- **0.5** Ложные `DisposableNotDisposed` задокументированы по месту
  ([`BackgroundTaskStore.cs`](src/RoslynCodeLens/BackgroundTasks/BackgroundTaskStore.cs),
  [`MultiSolutionManager.cs`](src/RoslynCodeLens/MultiSolutionManager.cs),
  [`SolutionChangeSafety.cs`](src/RoslynCodeLens/SolutionChangeSafety.cs)).

## Зачем

Убрать предупреждения компилятора и подтвердить/опровергнуть подозрения на утечки,
не меняя дизайн (этапы 1–7 не начинались; `SolutionManager` не тронут).

Два отклонения от буквы плана, оба вынужденные:

1. **«Точечное подавление» для `find_disposable_misuse` нереализуемо.** Инструмент —
   собственный анализатор проекта
   ([`FindDisposableMisuseLogic`](src/RoslynCodeLens/Tools/FindDisposableMisuseLogic.cs)),
   и [`IsDisposalAcknowledged()`](src/RoslynCodeLens/Tools/FindDisposableMisuseLogic.cs:185)
   распознаёт только `return x`, присваивание в поле/свойство и в `out`/`ref`.
   `[SuppressMessage]`, `#pragma`, атрибут-маркер не читаются — подавление в исходнике
   не влияет на вывод. Проверено эмпирически: документирующие комментарии в пункте 0.4
   не убрали находки (сдвинулись только номера строк).
2. **Переписывание `Task`-сайтов под распознаваемые формы опровергнуто тестами.**
   Для [`ChangeSignatureBridge`](src/RoslynCodeLens/Analysis/ChangeSignatureBridge.cs)
   «присваивание полю» реализовали через `[ThreadStatic]`-поле — поле не переживает
   `await` (продолжение на другом потоке пула), результат 28 падений
   `NullReferenceException` в `GetTaskResult`. Изменение откачено
   (`git checkout`), поведение оставлено исходным.

## Затронутые файлы

- `src/RoslynCodeLens/SolutionLoader.cs` — `RegisterWorkspaceFailedHandler` + `using`
  подписки; nullable-лямбды для `RunWithTimeoutAsync`.
- `src/RoslynCodeLens/Tools/ValidateEditLogic.cs` — привязка `newErrors` к baseline.
- `src/RoslynCodeLens/Metadata/IlDisassemblerAdapter.cs` — контракт владения `PEFile`.
- `src/RoslynCodeLens/Tools/PeekIlLogic.cs` — тот же контракт.
- `src/RoslynCodeLens/BackgroundTasks/BackgroundTaskStore.cs` — комментарий (освобождение
  ниже, через `removed.Dispose()`).
- `src/RoslynCodeLens/MultiSolutionManager.cs` — комментарий (владение передаётся).
- `src/RoslynCodeLens/SolutionChangeSafety.cs` — комментарий (задачи уходят в `Task.WhenAll`).
- `tests/RoslynCodeLens.Tests/LoaderConcurrencyHardeningTests.cs` — +1 тест (поток диагностик).
- `tests/RoslynCodeLens.Tests/ValidateEditLogicTests.cs` — +2 теста (обе ветки delta).
- `tests/RoslynCodeLens.Tests/Metadata/PEFileCacheTests.cs` — +3 теста (владение кэшем).
- `plans/2026-10-28-solid-architecture-audit.md` — отметки этапа 0 + раздел с примечаниями.

## Проверка

- `get_diagnostics` (severity=warning, проект `RoslynCodeLens`): **было 5 → стало 0**.
  Ошибок 0 на всех этапах.
- `find_disposable_misuse`: было 9 → стало 7, все ложные по конструкции
  (2×`Task` в `ChangeSignatureBridge`, 2×`Task` в `SolutionChangeSafety`, 2×`PEFile`
  во владении кэша, `PlainTextOutput` — не `IDisposable`, `BackgroundTaskStore`,
  `MultiSolutionManager`). Реальных нарушений 0.
- `dotnet test RoslynCodeLens.slnx`: **1245 passed / 0 failed / 0 skipped**
  (база 1239 + 6 новых тестов).
- Точечные прогоны: `LoaderConcurrencyHardeningTests` 7/7, `ValidateEditLogicTests` 9/9,
  `PEFileCacheTests` 5/5, `ChangeSignatureLogicTests` 51/51,
  `SolutionLoader|ParallelLoader|LegacySolution` 28/28.

## Риски / дальше

- Риск плана «`RegisterWorkspaceFailedHandler` меняет поток диагностик» **не реализовался**:
  `LoadDiagnostics` заполняется из `FindProjectsWithDroppedReferences`, а не из события;
  закреплено тестом.
- Остаток `find_disposable_misuse` (7) можно убрать только расширением самого анализатора —
  это вне этапа 0, отдельный пункт при необходимости.
- Пункт 5 плана помечен `[~]` (выполнен со сменой подхода), а не `[x]`.
