# Этап 5.3: `SolutionLoader` — сложность и декомпозиция

- Дата (UTC): 2026-10-28T23:30Z
- Режим/агент: code (скилл `solid-plan-implementation`)

## Что сделано

`SolutionLoader` (751 LOC, 22 члена, `OpenPerProjectAsync` CC 23 / cognitive 50) разобран по
двум задачам пункта, четырьмя коммитами. Тесты загрузчика **не правились**; добавлено 10 новых.

**Задача 1 — сложность `OpenPerProjectAsync`.** Два извлечения инструментом Roslyn
(`get_code_actions` → `apply_code_action` «Извлечь метод» → `rename_symbol`):

1. `BuildTargetGraph` (позже переехал в `TargetProjectGraph.Build`) — граф ProjectReference,
   ограниченный target-набором;
2. `BuildTargetGraph` — порядок корней по убыванию транзитивного замыкания (`OrderRootsFirst`);
3. `LoadProjectIntoCaptureAsync` — открытие проекта под `s_buildHostGate`, capture в
   `ConcurrentDictionary`, запись `Failed`/`Timeout` reasons.

**Задача 2 — LOC по ответственностям.** Три типа в `src/RoslynCodeLens/Loading/`, по одному
за коммит: `DetachedProjectFactory` (76 LOC), `SolutionFileLocator` (39 LOC),
`TargetProjectGraph` + `TargetGraph` record (84 LOC).

## Зачем

`OpenPerProjectAsync` держал сразу четыре вещи: построение графа, сортировку, параллельные
воркеры и обработку ошибок загрузки — при CC 23 его нельзя было менять без риска для
`Skipped`-контракта. Разнесение по фазам плана («построить граф», «открыть проект») убрало
и ветвления, и когнитивную нагрузку, не тронув поведение.

Форма — как в 5.1 и 5.2: фасад остаётся, подчинённые типы уезжают. Публичная поверхность
`SolutionLoader` сократилась только на `FindSolutionFile` (единственный потребитель —
`Program.cs`, тестов не было).

## Контракт `Skipped` — не изменён

Главный риск пункта, проверен `SolutionLoaderTests`, `SolutionLoaderFilterTests`,
`LegacySolutionHandlingTests`, `LoaderConcurrencyHardeningTests`, `SolutionLoaderTimeoutTests`
(46 тестов, все зелёные без единой правки):

| Ситуация | kind | reason |
|---|---|---|
| `ct` отменён | — (проброс исключения) | никогда не skip |
| Прочее исключение | `Failed` | `"{ExceptionType}: {Message}"` |
| Таймаут (`null` из `RunWithTimeoutAsync`) | `Timeout` | `"Project load exceeded {n}s. …"` |
| `legacy/missing/unknown` | `entry.Kind.ToString()` | `entry.Reason ?? "Unsupported project format."` |
| Не загрузился без записи в `failures` | `Failed` | `"Project could not be loaded."` |
| Исключён фильтром | `FilteredOut` | `"Excluded by load_solution filter."` |

Подписка `RegisterWorkspaceFailedHandler` (этап 0, пункт 1) осталась в том же `using`-скоупе
относительно своего `MSBuildWorkspace` — и в solution-level, и в per-project ветке.

## Метрика до/после

| Метрика | До | После |
|---|---|---|
| `OpenPerProjectAsync` — CC | **23** | **14** |
| `OpenPerProjectAsync` — cognitive | **50** | **26** |
| `SolutionLoader` — LOC | **751** | **670** |
| `SolutionLoader` — членов | 22 | **19** |
| Методов `SolutionLoader*` с CC > 15 | 1 | **0** |
| `get_diagnostics` | 0 / 0 | **0 / 0** |
| `dotnet test RoslynCodeLens.slnx` | 1276 passed | **1286 passed, 0 failed** |
| Тесты загрузчика | 46 | 46 **без правок** |

Критерий «`OpenPerProjectAsync` отсутствует в `get_complexity_metrics(threshold=15)`» —
**достигнут**: метод из списка ушёл (14 < 15).

## ⚠️ Критерий «`SolutionLoader` вне `find_large_classes(300, 15)`» не достигнут

670 LOC при пороге 300. Члены (19) и сложность в норме; остаток — 4 связные политики,
каждая из которых не дробится без ломки поведения:

- `OpenAsync` (CC 13) — retry-по-деградации: 3 попытки, выбор лучшей, `AnalyzerSkew`, fallback;
- `OpenPerProjectAsync` (CC 14) — оркестрация воркеров + ре-ститч снимка (остались `await`/локи);
- `CompileAllParallelAsync` + `GetCompilationLevels` — фаза компиляции;
- `SolutionOpen`, `RunWithTimeoutAsync`, диагностики загрузки.

Следующий очевидный шаг — `SolutionCompiler` для фазы компиляции. Это **не** декомпозиция
`OpenPerProjectAsync`, а новый пункт, поэтому в 5.3 не делалось (запрет «не смешивать пункты»).

## Ключевая находка: `rename_symbol` переписал чужой XML-doc

При переименовании `NewMethod` → `BuildTargetGraph` инструмент с `renameInComments: true`
изменил XML-doc в [`ExtractMethodLogic.cs`](../src/RoslynCodeLens/Tools/ExtractMethodLogic.cs):
там `NewMethod` — это имя, которое генерирует **сама Roslyn** для извлечённого метода
(`GeneratedNamePrefix`). Правка откатана (`git checkout --`), файл в итоговый коммит не попал.

Урок: для общих имён (`NewMethod`, `Helper`, `Process`) preview надо читать по **всем** файлам
из `edits`, а не только по целевому — `conflicts` это не поймает, ошибки компиляции тоже.

Вторая находка: Roslyn extract-method возвращает метод **без** `async`, даже когда тело
содержит `await` (провайдер не знает о `async`-контексте). Сигнатура `bool` →
`async Task<bool>` дописана вручную; `conflicts` были пусты, потому что превью не компилирует.

## Затронутые файлы

- `src/RoslynCodeLens/SolutionLoader.cs` — 751 → 670 LOC; два извлечения, `FindSolutionFile`
  и `DetachedProjectFactory`-методы вынесены.
- `src/RoslynCodeLens/Loading/DetachedProjectFactory.cs` — новый.
- `src/RoslynCodeLens/Loading/SolutionFileLocator.cs` — новый.
- `src/RoslynCodeLens/Loading/TargetProjectGraph.cs` — новый (+ `TargetGraph` record).
- `src/RoslynCodeLens/Program.cs` — `SolutionLoader.FindSolutionFile` → `SolutionFileLocator.Find`.
- `tests/RoslynCodeLens.Tests/SolutionFileLocatorTests.cs` — 5 новых тестов.
- `tests/RoslynCodeLens.Tests/TargetProjectGraphTests.cs` — 5 новых тестов.
- `plans/2026-10-28-solid-architecture-audit.md` — раздел 5.3, галочка в таблице этапа.

## Проверка

- Красный → зелёный по каждому типу: CS0246 на отсутствующий тип, затем 5/5 и 5/5.
- `get_diagnostics` (severity=warning, проект `RoslynCodeLens`) — **0/0** после каждого шага.
- Точечные прогоны: загрузчик 46/46 (дважды), `SolutionFileLocatorTests` 5/5,
  `TargetProjectGraphTests`+загрузчик 34/34.
- `dotnet test RoslynCodeLens.slnx` — **1286 passed, 0 failed** (8 m 23 s).
- `get_complexity_metrics(15)` — `OpenPerProjectAsync` отсутствует (14); ни один метод
  `SolutionLoader*` не выше 15.
- `find_large_classes(300, 15)` — новых типов нет в списке; `SolutionLoader` 670 LOC
  (остаток зафиксирован выше).

## Риски / дальше

- Один тест был написан неверно автором (не кодом): `OrderRootsFirst` считает внешние
  (не-target) рёбра в размер замыкания — поведение исходного кода, зафиксировано тестом
  `OrderRootsFirst_CountsReferencesOutsideTheTargetSetInClosureSize` + отдельный
  `Build_ExcludesReferencesOutsideTheTargetSet` на фильтрацию рёбер.
- `SolutionLoader` остаётся крупнейшим классом решения (670 LOC). Кандидат на следующий
  пункт — `SolutionCompiler`.
