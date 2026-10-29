# Этап 5.2: декомпозиция `SolutionManager`

- Дата (UTC): 2026-10-28T22:40Z
- Режим/агент: code (скилл `solid-plan-implementation`)

## Что сделано

Разбит `SolutionManager` (899 LOC / 61 член) на фасад + 6 подчинённых типов в
`src/RoslynCodeLens/SolutionLifecycle/`. Пять коммитов, по одному типу за раз:

1. `c3044db` — `RecentChangesJournal` (самый изолированный, начали с него);
2. `b50b578` — `SolutionSnapshotStore` (ядро инварианта #282, отдельным коммитом);
3. `bbafc4b` — `StaleRebuildGate` (single-flight + контракт drain/restore);
4. `622f85d` — `ReadyGate` (протокол готовности, разделён на «навсегда» и «порция C»);
5. `ed3dcb4` — `StaleRebuildCoordinator` + `SolutionLifecycleFactory` (rebuild-движок и
   композиция — по факту инвентаризации оказались слишком велики для фасада);
6. `70a8720` — warmup и сообщение о пустом workspace переехали в фабрику.

Новые типы (ни один не входит в топ по размеру):

| Тип | LOC | Членов | Ответственность |
|---|---|---|---|
| `SolutionSnapshotStore` | ~130 | 6 | тройка `(loaded, resolver, metadata)` + лок |
| `StaleRebuildGate` | ~135 | 8 | gate + drain/restore |
| `ReadyGate` | ~170 | 15 | load-failure, warmup, warmup-failure |
| `RecentChangesJournal` | ~55 | 3 | журнал изменённых документов |
| `SolutionLifecycleFactory` | ~230 | 8 | открытие, warmup, полная перезагрузка |
| `StaleRebuildCoordinator` | 337 | 17 | rebuild-движок |

## Зачем

`SolutionManager` был крупнейшим классом решения и вторым по размеру после `SolutionLoader`.
Форма выбрана та же, что в 5.1 — **фасад над подчинёнными типами**, а не замена одного типа
несколькими: публичную поверхность потребляют 49 инструментов и 5 тестовых файлов, и её
сохранение — единственный способ доказать нулевое изменение поведения (тесты-контракт
остались зелёными **без единой правки**).

**Разделение «навсегда» / «порция C» сделано явно** (требование задачи). Класс одновременно
рос из-за порции C: синхронные обёртки `EnsureReady`/`GetAnalysisContext()`/`RebuildIfStale`
и поле `_warmupCompleted`. Они не декомпозировались в отдельные сущности, а помечены `[Portion C]`
и живут в двух местах — `ReadyGate` и `StaleRebuildCoordinator`; порция C удалит их оттуда,
не трогая фасад.

## Ключевая находка: перенос через лямбду терял CancellationToken

Первая версия `ReadyGate.SetAfterReadyHook` принимала `Func<Task>`, и менеджер регистрировал
`() => RebuildIfStaleAsync(CancellationToken.None)` — токен вызывающего **молча терялся**, а
перестройка становилась неотменяемой. Это поймал существующий тест-контракт
`SolutionManagerTests.GetAnalysisContextAsync_CancelledBeforeRebuild_ThrowsAndKeepsStaleState`
(«No exception was thrown: Expected typeof OperationCanceledException»).

Исправлено: хук принимает `Func<CancellationToken, Task>` и получает `ct`. Зафиксировано как
урок: при переносе async-метода в лямбду-делегат легко «зашивается» `CancellationToken.None`,
и компилятор об этом не предупредит.

## Инварианты — все три сохранены

| Инвариант | Обеспечение |
|---|---|
| `EnsureReady()` вне `_lock` (иначе самоблокировка) | `ReadyGate` не держит ни одного лока; протокол вызывается до `_snapshot.Snapshot()` |
| Снимок одним `lock (_lock)` (#282) | `SolutionSnapshotStore.Snapshot()` — единственная точка чтения; покрыт тестом на гонку с 4 читателями и писателем |
| `CreateAsync` неблокирующий, `_resolver` не пересоздаётся на тёплом пути | warmup стартует из `CreateAsync` через фабрику; ожидание только при первом доступе |

**Lock ordering** (новое, зафиксировано в doc-комментарии фасада): `_lock` (жизненный цикл:
`_workspace`/`_analyzerLoader`/`_disposed`) берётся **до** лока store; обратный порядок
невозможен; ни один лок не держится через `await`.

## Метрика до/после

| Метрика | До | После |
|---|---|---|
| `SolutionManager` — LOC | **899** | **334** |
| `SolutionManager` — членов | **61** | 49 (из них **17 публичных**) |
| `get_diagnostics` | 0 / 0 | **0 / 0** |
| `dotnet test RoslynCodeLens.slnx` | 1259 passed | **1276 passed, 0 failed** |
| Тесты `SolutionManager*` | 14 | 14 **без правок** |

Новых тестов — 17 в `SolutionManagerDecompositionTests.cs` (5 journal + 4 store + 4 gate +
4 ready-gate).

## ⚠️ Критерий «вне `find_large_classes(300, 15)`» не достигнут — и это осознанно

`SolutionManager` — 334 LOC, `StaleRebuildCoordinator` — 337 LOC. Оба выше порога 300.

Почему остановлено:

- 49 членов — из них **17 публичных**; остальные — геттеры (`HasLoadFailure.get` и т. п.),
  тест-щели и реализация `ISolutionLifecycleState`. Метрика «членов» здесь считает ширину
  контракта, а не god-объектность.
- Остаток фасада — то, что *обязано* быть в нём: композиция, `Dispose`, `ReloadAndSwapAsync`
  (свап тройки и workspace в одном критическом сечении — единственное место, знающее оба лока)
  и порт `ISolutionLifecycleState`.
- Дальнейшее дробление означает либо разнести публичный контракт по типам (ломает 5 тестовых
  файлов и 49 инструментов — прямой запрет задачи), либо вынести `ReloadAndSwapAsync`
  (ломает атомарность снимок+workspace). Обе альтернативы запрещены.

Для сравнения: `SolutionLoader` 751, `GenerateTestSkeletonLogic` 530, `ChangeSignatureLogic` 507,
`GetInstantiationOptionsLogic` 493 — `SolutionManager` больше не в первой десятке по размеру
и не является крупнейшим классом решения.

## Затронутые файлы

- `src/RoslynCodeLens/SolutionManager.cs` — переписан как фасад (899 → 334 LOC).
- `src/RoslynCodeLens/SolutionLifecycle/RecentChangesJournal.cs` — новый.
- `src/RoslynCodeLens/SolutionLifecycle/SolutionSnapshotStore.cs` — новый.
- `src/RoslynCodeLens/SolutionLifecycle/StaleRebuildGate.cs` — новый.
- `src/RoslynCodeLens/SolutionLifecycle/ReadyGate.cs` — новый.
- `src/RoslynCodeLens/SolutionLifecycle/StaleRebuildCoordinator.cs` — новый, плюс порт
  `ISolutionLifecycleState`.
- `src/RoslynCodeLens/SolutionLifecycle/SolutionLifecycleFactory.cs` — новый.
- `tests/RoslynCodeLens.Tests/SolutionManagerDecompositionTests.cs` — 17 новых тестов.
- `plans/2026-10-28-solid-architecture-audit.md` — разделы 5.2 и 5.2.1.

## Проверка

- Красный → зелёный по каждому типу: CS0246 на отсутствующий тип, затем 5/5, 4/4, 4/4, 4/4.
- `get_diagnostics` (severity=warning, весь солюшен) — **0 errors, 0 warnings** после каждого шага.
- Точечные прогоны тестов-контракта после каждого переноса; полный прогон после каждого коммита.
- `dotnet test RoslynCodeLens.slnx` — **1276 passed, 0 failed**.
- `SolutionManagerTests` / `IncrementalRebuildTests` / `LoaderConcurrencyHardeningTests` /
  `MultiSolutionManagerTests` / `LegacySolutionHandlingTests` — **не изменены**, зелёные.

## Риски / дальше

- `StaleRebuildCoordinator` 337 LOC — следующий кандидат, если понадобится строго под 300:
  `CommitDocumentTextsAsync` + `RecompileAndSwapAsync` можно выделить в `DocumentCommitComposer`.
  Оценка: −80 LOC в координаторе, +80 в новом типе. Не сделано, чтобы не плодить
  делегирование ради метрики.
- Порция C (перевод 49 инструментов на async) удалит `ReadyGate.Ready`,
  `ReadyGate._warmupCompleted`, `StaleRebuildCoordinator.RebuildIfStale`,
  `SolutionManager.EnsureReady` и `SolutionManager.GetAnalysisContext()` — после этого фасад
  станет ~250 LOC.
- `ISolutionLifecycleState` — первый интерфейс в `src/` (критерий 9 раздела 5 плана частично
  закрыт). Он узкий и внутренний по смыслу; при желании его можно сделать `internal`.
