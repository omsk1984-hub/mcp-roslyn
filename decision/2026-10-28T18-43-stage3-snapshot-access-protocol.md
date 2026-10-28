# Этап 3: единый протокол доступа к снапшоту (D1, D3, D4, D6)

- Дата (UTC): 2026-10-28T18:43Z
- Режим/агент: code
- План: [`plans/2026-10-28-solid-architecture-audit.md`](../plans/2026-10-28-solid-architecture-audit.md), этап 3

## Что сделано

- **Пункт 1** (`d21993e`): четыре характеристических теста в
  [`SolutionManagerTests.cs`](../tests/RoslynCodeLens.Tests/SolutionManagerTests.cs),
  зелёные **на неизменённом проде** — фиксируют поведение `GetLoadedSolution`,
  `GetResolver`, `GetMetadataResolver`, `GetAnalysisContext` при (а) `HasLoadFailure`
  и (в) stale-проектах, плюс отдельно текущее поведение `GetIlDisassembler()`.
- **Пункты 2+3** (`7774382`): префикс `ThrowIfLoadFailed → ожидание warmup → обработка
  провала warmup → RebuildIfStale` свёрнут в приватный
  [`EnsureReady()`](../src/RoslynCodeLens/SolutionManager.cs). Строка
  `"Solution warmup failed."` вынесена в `const string WarmupFailedMessage`.
  Дополнительно на `EnsureReady()` переведён `EnsureLoaded()` — он содержал пятую копию
  того же префикса (в плане не значился, но мешал критерию «протокол в одном экземпляре»).
  `GetAnalysisContext()` = `EnsureReady()` **до** `lock (_lock)`, затем захват лока и
  чтение трёх полей — атомарность снимка сохранена.
- **Пункты 4+6** (`c97ea40`): `MultiSolutionManager` читает снапшот через
  `GetAnalysisContext()`; `GetLoadedSolution()` стал проекцией снапшота.
  `SolutionManager.GetResolver` и `GetMetadataResolver` удалены. Перенацелены
  сайты: `InspectExternalAssemblyTool` (`…Metadata`), 2 теста
  `MultiSolutionManagerTests`, 6 мест `SolutionManagerTests`.
- **Пункт 5** (`39d2bcc`): `GetIlDisassembler()` оставлен **осознанным исключением**
  из протокола — см. ниже.

## Решения и обоснования

### Почему warmup остался в `EnsureReady()`, а не в `CreateAsync`

Тест `CreateAsync_ReturnsBeforeCompilationCompletes` закрепляет неблокирующий старт:
`CreateAsync` запускает warmup в фоне и возвращается сразу. Перенос ожидания в
`CreateAsync` сломал бы запуск сервера, поэтому блокирующее ожидание выполняется
только при **первом** обращении к снапшоту — ровно как было до рефакторинга.

### Почему `EnsureReady()` не пересоздаёт `_resolver`

Тест `GetResolver_ReturnsCachedInstance_WhenNotStale` требует идентичности экземпляра
при отсутствии stale-проектов. `EnsureReady()` только ждёт и вызывает
`RebuildIfStale()`, который при `HasStaleProjects == false` выходит сразу, — резолверы
создаются исключительно в путях перестройки (`WarmupAsync`, `RecompileAndSwapAsync`,
`RebuildViaFullReloadAsync`).

### Почему `EnsureReady()` вызывается ДО `lock (_lock)`

И `WarmupAsync`, и `RebuildIfStale()` / `RecompileAndSwapAsync` захватывают `_lock`
сами. Взятие лока перед `EnsureReady()` дало бы самоблокировку. Лок берётся после —
именно он и обеспечивает атомарность трёх полей (фикс рассинхронизации из #282).

### `GetIlDisassembler()` (пункт 5) — решение по факту

`IlDisassemblerAdapter` держит **только** `PEFileCache` (конструктор
`IlDisassemblerAdapter(PEFileCache cache)`), а `DisassembleMethod` работает по
`assemblyPath` + `metadataToken`. Состояние загруженного решения он **не читает** —
значит, `EnsureReady()` не добавил бы корректности, но **сломал** бы поддерживаемый
сценарий: `peek_il` по closed-source сборке, где загружать может быть просто нечего.
Поэтому аксессор оставлен вне протокола, с комментарием-обоснованием в коде и
переименованным тестом (снят прежний ярлык «DefectD3New»).

## Находки

- **Сценарий (б) «упавший warmup» не покрыт тестом.** Детерминированной тест-симы для
  инъекции сбоя warmup в `SolutionManager` нет: `CompileAllParallelAsync` на фикстурах
  не бросает, а добавлять боевой хук ради теста не стали (решение пользователя).
  Поведение зафиксировано в коде и в комментарии тестов, но не проверяется
  исполняемым тестом.
- **Сценарий (а) воспроизводится только через невалидный путь.** `HasLoadFailure`
  возникает лишь когда бросит `SolutionLoader.OpenAsync`; несуществующий или битый
  `.sln/.slnx` даёт *пустое*, а не *упавшее* решение (парсинг и per-project fallback
  глотают ошибки). Тест использует путь с NUL-символами — падает
  `Path.GetFullPath` в `ProjectClassifier.EnumerateProjects`, вне try/catch. Побочный
  вывод для этапа 6: эта ветка почти не исполняется в реальной эксплуатации.
- **Копий протокола было пять, а не четыре.** `EnsureLoaded()` содержал ту же
  последовательность; при рефакторинге только четырёх сайтов критерий
  «протокол в одном экземпляре» не достигался. Сведён вместе с остальными —
  поведение не изменилось (его дополнительная проверка `_loaded.IsEmpty` осталась).

## Затронутые файлы

- `src/RoslynCodeLens/SolutionManager.cs` — `EnsureReady()`, `const WarmupFailedMessage`,
  удалены `GetResolver`/`GetMetadataResolver`, документация `GetIlDisassembler`,
  `GetAnalysisContext`/`EnsureLoaded` переведены на протокол
- `src/RoslynCodeLens/MultiSolutionManager.cs` — геттеры через `GetAnalysisContext()`
- `src/RoslynCodeLens/Tools/InspectExternalAssemblyTool.cs` — `…Metadata` вместо удалённого геттера
- `tests/RoslynCodeLens.Tests/SolutionManagerTests.cs` — 4 характеристических теста + перенацеливание
- `tests/RoslynCodeLens.Tests/MultiSolutionManagerTests.cs` — 2 сайта на `GetAnalysisContext()`
- `plans/2026-10-28-solid-architecture-audit.md` — отметки этапа 3

## Проверка

- `get_diagnostics`: 0 errors / 0 warnings (до и после)
- `find_references`: `SolutionManager.GetResolver` = 0, `SolutionManager.GetMetadataResolver` = 0,
  `MultiSolutionManager.GetResolver` = 0, `MultiSolutionManager.GetMetadataResolver` = 0
- Копий протокола в `src/`: ровно одна (`_warmupTask?.GetAwaiter().GetResult()` только в `EnsureReady`)
- `dotnet test RoslynCodeLens.slnx`: **1249 passed / 0 failed** (база 1245 + 4 новых), прогон до и после каждого пункта

## Риски / дальше

- Этап 4 (async-first) разблокирован: `EnsureReady()` — единственная точка, которую нужно
  превратить в `EnsureReadyAsync()`; синхронный over-async остался только там.
- Сценарий (б) стоит покрыть, когда появится законный тест-сим (например, интерфейс
  загрузчика, подменяемый в тестах) — это естественно ложится на этап 6 (тестируемость).
- `MultiSolutionManager.GetLoadedSolution()` теперь тоже тянет `MetadataSymbolResolver`
  из снапшота — лишняя работа, но снапшот и так конструируется целиком;
  на профиле не сказывается.
