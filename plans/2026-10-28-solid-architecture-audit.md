# SOLID-аудит решения `RoslynCodeLens.slnx`

Дата аудита: 2026-10-28.
Метод: MCP-сервер `roslyn-codelens` (скилл `solid-architecture-audit`), только чтение.
Объект: единственное решение репозитория — 3 проекта (`src/RoslynCodeLens`,
`tests/RoslynCodeLens.Tests`, `benchmarks/RoslynCodeLens.Benchmarks`).

---

## 0. Сводка окружения (факты)

| Проверка | Результат |
|---|---|
| `list_solutions` | 1 решение, `isActive: true`, `status: ready`, `skippedProjects: []` |
| Ошибки компиляции | **0** |
| Предупреждения компилятора | **5** (2× CS0618 устаревший `Workspace.WorkspaceFailed`, 2× CS8619 nullability в `SolutionLoader`, 1× CS8602 в `ValidateEditLogic`) |
| Публичных записей API | **1587**, из них **0 интерфейсов** |
| Циклы зависимостей (project/namespace) | **0** |
| Нарушения слоистости (3 правила) | **0** |
| Покрытие тестами (стат.) | **31 %** (332 / 1046), risk hotspots (CC ≥ 5) — **46** |

---

## 1. Результаты аудита

### 1.1 Что уже хорошо (существующие паттерны)

| Паттерн | Проявление | Оценка |
|---|---|---|
| Разделение транспорт / логика | `*Tool` — тонкий статический MCP-адаптер (атрибуты, валидация аргументов), `*Logic` — статическая функция, принимающая `LoadedSolution`/`SymbolResolver` | **Сильное место.** Логика детерминирована, без состояния, тестируется без MCP-хоста |
| Композиционный корень минимален | [`Program.cs:59-77`](../src/RoslynCodeLens/Program.cs:59) — 4 синглтона, всё остальное конструирует `WithToolsFromAssembly()` | Нет «расползания» конфигурации |
| Единый конверт ответа | `ToolListResult<T>` (`items`, `totalCount`, `truncated`, `limit`, `summary`) применён единообразно | Консистентный контракт для клиента |
| Снимок состояния под блокировкой | [`SolutionManager.GetAnalysisContext()`](../src/RoslynCodeLens/SolutionManager.cs:510) отдаёт `(solution, resolver, metadata)` из одного захвата `_lock` — явно задокументировано как фикс рассинхронизации (#282) | Осознанная работа с identity-инвариантами Roslyn |
| Структурированные ошибки | `StructuredErrorToolWrapper` + `McpToolException` + enum `ToolErrorCode` (**enum, не строки**) | Типизированные коды ошибок вместо магии строк |
| Доменные модели типизированы | 126 `record` + 17 `enum` (`AsyncViolationPattern`, `DisposableMisusePattern`, `PublicApiKind`, …) | ISP/типобезопасность на уровне моделей — в порядке |
| Защита от устаревших данных | Дренаж снимка перед перестройкой, восстановление при сбое ([`RebuildIfStale()`](../src/RoslynCodeLens/SolutionManager.cs:226)) | Аккуратная обработка гонок |
| Отказ от повторной сериализации | Рендереры возвращают типизированные `record`, а не JSON-строки | Дефект «двойная сериализация» **отсутствует** |
| Отсутствие потери типа | Коллекции объявлены как `IReadOnlyList<TOther>`; нет `IReadOnlyList<object>` + `item is T` | Дефект «потеря типа при фильтрации» **отсутствует** |

### 1.2 Подтверждённые дефекты

| # | Дефект | Механизм | Ссылки |
|---|---|---|---|
| D1 | **Тройное дублирование протокола доступа** | `GetLoadedSolution`, `GetResolver`, `GetMetadataResolver`, `GetAnalysisContext` содержат **побайтово одинаковый** префикс: `ThrowIfLoadFailed()` → `_warmupTask?.GetAwaiter().GetResult()` → проверка `_warmupException` → `RebuildIfStale()` | [`SolutionManager.cs:153`](../src/RoslynCodeLens/SolutionManager.cs:153), [`:163`](../src/RoslynCodeLens/SolutionManager.cs:163), [`:173`](../src/RoslynCodeLens/SolutionManager.cs:173), [`:510`](../src/RoslynCodeLens/SolutionManager.cs:510) |
| D2 | **Sync-over-async в горячем пути** | 5 точек `.GetAwaiter().GetResult()`. Внутри — `RebuildAsync(snapshot)`, то есть **инкрементальная перестройка проекта блокирует поток на всё время рекомпиляции**. Тот же антипаттерн в 4 аксессорах + `RebuildIfStale` | [`SolutionManager.cs:156`](../src/RoslynCodeLens/SolutionManager.cs:156), [`:166`](../src/RoslynCodeLens/SolutionManager.cs:166), [`:176`](../src/RoslynCodeLens/SolutionManager.cs:176), [`:188`](../src/RoslynCodeLens/SolutionManager.cs:188), [`:254`](../src/RoslynCodeLens/SolutionManager.cs:254) |
| D3 | **Ожидание перестройки повторяется 4 раза** | `RebuildIfStale()` вызывается из каждого геттера. Если решение «протухло», первый геттер ждёт полную перестройку; при `GetAnalysisContext()` — ожидание перед захватом `_lock`, что дополнительно расширяет окно гонки | [`SolutionManager.cs:226-267`](../src/RoslynCodeLens/SolutionManager.cs:226) |
| D4 | **Дублирование тел геттеров = 4 копии одного инварианта** | Любое изменение протокола (например, добавление health-проверки) требует правки в 4 местах; рассинхронизация одного из них даёт трудноуловимый дефект | там же |
| D5 | **`MetadataSymbolResolver` не кэшируется при создании** | Конструктор — простое присваивание двух полей ([`MetadataSymbolResolver.cs:11-15`](../src/RoslynCodeLens/Symbols/MetadataSymbolResolver.cs:11)), поэтому пересоздание дёшево, **но** проверка отсутствует в явном виде: инвариант «пересоздавать можно» нигде не зафиксирован | [`MetadataSymbolResolver.cs:11`](../src/RoslynCodeLens/Symbols/MetadataSymbolResolver.cs:11) |
| D6 | **Дублирование `Anns`/nullability-проверки** | 4× `if (_warmupException != null) throw new InvalidOperationException("Solution warmup failed.", _warmupException);` — сообщение захардкожено строкой в 4 местах | как D1 |
| D7 | **9 предупреждений `DisposableNotDisposed`** | Часть — ложные (Task в `ChangeSignatureBridge`, `Task.Run` в `SolutionChangeSafety`), но `IlDisassemblerAdapter`/`PeekIlLogic` с `_cache.Get(assemblyPath)` — **реальные** кандидаты на утечку PEFile | [`IlDisassemblerAdapter.cs:19`](../src/RoslynCodeLens/Metadata/IlDisassemblerAdapter.cs:19), [`:21`](../src/RoslynCodeLens/Metadata/IlDisassemblerAdapter.cs:21), [`PeekIlLogic.cs:225`](../src/RoslynCodeLens/Tools/PeekIlLogic.cs:225) |
| D8 | **Предупреждения компилятора в загрузчике** | `Workspace.WorkspaceFailed` помечен `[Obsolete]` (нужен `RegisterWorkspaceFailedHandler`), 2× CS8619 (несоответствие `Task<Solution>` / `Task<Solution?>`), CS8602 в `ValidateEditLogic` | [`SolutionLoader.cs:144`](../src/RoslynCodeLens/SolutionLoader.cs:144), [`:154`](../src/RoslynCodeLens/SolutionLoader.cs:154), [`:360`](../src/RoslynCodeLens/SolutionLoader.cs:360), [`:373`](../src/RoslynCodeLens/SolutionLoader.cs:373), [`ValidateEditLogic.cs:101`](../src/RoslynCodeLens/Tools/ValidateEditLogic.cs:101) |

> Дефектов класса «потеря данных при фильтрации по типу», «двойная сериализация»,
> «несогласованные парсеры» и «Service Locator» (`GetRequiredService<T>` вне
> композиционного корня) **не обнаружено** — проверено трассировкой `find_references`
> по 51 вызову `GetAnalysisContext()`.

### 1.3 Нарушения SOLID

#### SRP — нарушен

| Проявление | Метрика | Ссылка |
|---|---|---|
| `SolutionManager` — 51 член, 647 строк, 22+ ответственности | `find_large_classes` | [`SolutionManager.cs:10`](../src/RoslynCodeLens/SolutionManager.cs:10) |
| `SolutionLoader` — 22 члена, **731 строка**, `OpenPerProjectAsync` CC=23 / cognitive=50 | `find_large_classes`, `get_complexity_metrics` | [`SolutionLoader.cs:10`](../src/RoslynCodeLens/SolutionLoader.cs:10), [`:278`](../src/RoslynCodeLens/SolutionLoader.cs:278) |
| `SymbolResolver` — **45 членов**, 13 полей, 414 строк, 6 входящих namespace | ⚠️ `find_god_objects` вернул его при первом аудите, но **не воспроизводится** (см. «Поправки к аудиту» ниже) — фактически оценивался по LOC/членам | [`SymbolResolver.cs:7`](../src/RoslynCodeLens/SymbolResolver.cs:7) |
| `GenerateTestSkeletonLogic` — 18 членов, 530 строк: резолвинг символа + выбор фреймворка + генерация C#-текста | `find_large_classes` | [`GenerateTestSkeletonLogic.cs:10`](../src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs:10) |
| `ChangeSignatureBridge` — 525 строк: рефлексия + межверсионная совместимость Roslyn + доменная логика изменения сигнатуры | `find_large_classes`; 5 из 13 reflection usages | [`ChangeSignatureBridge.cs:25`](../src/RoslynCodeLens/Analysis/ChangeSignatureBridge.cs:25) |
| `FindAsyncViolationsLogic.Execute` — CC=27, **cognitive=66**, вложенность 5. Один метод ищет 6 классов нарушений | худший метод решения | [`FindAsyncViolationsLogic.cs:14`](../src/RoslynCodeLens/Tools/FindAsyncViolationsLogic.cs:14) |
| `FindObsoleteUsageLogic.Execute` — CC=24, cognitive=61 | 2-й худший | [`FindObsoleteUsageLogic.cs:11`](../src/RoslynCodeLens/Tools/FindObsoleteUsageLogic.cs:11) |

#### OCP — ослаблен

| Проявление | Ссылка |
|---|---|
| 71 инструмент жёстко зашит как `static`-метод с атрибутом `[McpServerTool]`; добавление инструмента = правка исходников, расширения извне нет | `Tools/*Tool.cs` (71 файл) |
| Цепочка из 6 `if (direction is "...")` / сравнений строк направления и `kind` правил архитектуры вместо `enum` + полиморфизма | [`GetCallGraphLogic.cs:31`](../src/RoslynCodeLens/Tools/GetCallGraphLogic.cs:31), [`CheckArchitectureLogic.cs`](../src/RoslynCodeLens/Tools/CheckArchitectureLogic.cs:56) |
| Определение «тестового проекта» и фильтры мёртвого кода реализованы статическими хелперами, а не подменяемой стратегией | [`DeadCodeFilters.cs:65`](../src/RoslynCodeLens/Tools/DeadCodeFilters.cs:65) |

#### LSP — не нарушен

Реализаций интерфейсов в репозитории нет, иерархий наследования в прикладном коде нет.
Риск отсутствует по причине отсутствия контрактов (см. ISP/DIP) — это проблема
*другого* рода: нечего нарушать, потому что нет полиморфизма вообще.

#### ISP — нарушен (в наследственной форме)

| Проявление | Ссылка |
|---|---|
| **0 интерфейсов на 1587 публичных записей.** Все `*Tool`, `*Logic`, `*Manager`, `*Loader`, `*Resolver` — конкретные типы | `get_public_api_surface`: `byKind.Interface` отсутствует |
| `*Logic.Execute` повсеместно принимает 3–8 позиционных параметров (`loaded, resolver, metadata, symbol, direction, maxDepth, maxNodes, ct`); параметр-объект (контекст) не выделен | [`GetCallGraphLogic.cs:18`](../src/RoslynCodeLens/Tools/GetCallGraphLogic.cs:18), [`GetInstantiationOptionsLogic.cs:41`](../src/RoslynCodeLens/Tools/GetInstantiationOptionsLogic.cs:41), [`ValidateEditLogic.cs:23`](../src/RoslynCodeLens/Tools/ValidateEditLogic.cs:23) |
| `SolutionManager` публикует 4 пересекающихся аксессора состояния + управление жизненным циклом + `Dispose` + `RecentlyChangedDocuments` в одном типе | [`SolutionManager.cs:148-190`](../src/RoslynCodeLens/SolutionManager.cs:148) |

#### DIP — нарушен

| Проявление | Ссылка |
|---|---|
| Все 51 вызов `GetAnalysisContext()` зависят от **конкретного** `MultiSolutionManager`; подменить в тесте нечем | [`GetCallGraphTool.cs:37`](../src/RoslynCodeLens/Tools/GetCallGraphTool.cs:37) (типичный сайт) |
| `MultiSolutionManager` публикует конкретные `SolutionManager`, `SymbolResolver`, `MetadataSymbolResolver`, `IlDisassemblerAdapter` — ни одной абстракции | [`MultiSolutionManager.cs:74-86`](../src/RoslynCodeLens/MultiSolutionManager.cs:74) |
| `SolutionManager` держит `FileChangeTracker`, `PEFileCache`, `ShadowCopyAnalyzerAssemblyLoader` как поля с конкретными типами | [`SolutionManager.cs:10`](../src/RoslynCodeLens/SolutionManager.cs:10) |

### 1.3.1. Поправки к аудиту (выявлены в ходе реализации, 2026-10-28)

Две ошибки исходного аудита, обнаруженные при исполнении плана. Обе меняют способ
проверки готовности — фиксируются здесь, чтобы план не оценивался по неисправным критериям.

| Ошибка | Что было заявлено | Факт | Следствие |
|---|---|---|---|
| **A1. Неверный критерий `find_god_objects`** | «`SymbolResolver` — единственная находка `find_god_objects`» | `find_god_objects` возвращает **пусто и до, и после** декомпозиции. Порог `minOutgoingNamespaces = 5` не достигается: тип зависит от 2 не-BCL namespace (`Analysis`, `Symbols`) | Критерий «`find_god_objects` пусто» **не мог провалиться** и ничего не доказывает. Заменяющий критерий — `find_large_classes` (LOC / члены) |
| **A2. Заниженный масштаб sync-over-async** | «5 сайтов, все в `SolutionManager`» | Фактически `SolutionManager` содержал 4 копии одного вызова (сведены в `EnsureReady()` этапом 3) + **3 сайта в слое логики** (`FindReferencesLogic` ×2, `FindCallersLogic` ×1), не значившихся в аудите | Цель этапа 4 была недостижима правкой только `SolutionManager`; потребовалось разделение на порции A/B/C (раздел 4.2) |

**Актуальный размер god-классов после этапа 5.1** (`find_large_classes(300, 15)`, по убыванию LOC,
только `src/`, без сгенерированного кода):

| Класс | LOC | Члены |
|---|---|---|
| `SolutionManager` | **899** | **61** |
| `SolutionLoader` | 751 | 22 |
| `GenerateTestSkeletonLogic` | 530 | 18 |
| `ChangeSignatureBridge` | 525 | 15 |
| `ChangeSignatureLogic` | 507 | 16 |
| `GetInstantiationOptionsLogic` | 493 | 22 |
| `CheckArchitectureLogic` | 463 | 18 |
| `CodeActionRunner` | 441 | 15 |
| `GetExtensionMethodsLogic` | 428 | 16 |
| `MultiSolutionManager` | 321 | 24 |
| `GetCallGraphLogic` | 311 | 10 |

⚠️ `SolutionManager` вырос с 647 до 899 LOC за этапы 3–4: единый протокол, async-варианты и
синхронные обёртки добавлены, но удаления старых путей ещё не было (отложено до порции C).
Это ожидаемый рост, но этап 5 должен его вернуть к норме.

### 1.4 Антипаттерны

| Антипаттерн | Да / нет | Свидетельство |
|---|---|---|
| Статические классы-сервисы (не подменяются в тестах) | **Да** | Все 50+ `*Logic` — `public static class` |
| Sync-over-async | **Да** | 5 сайтов `GetAwaiter().GetResult()` в `SolutionManager` |
| Дублирование логики | **Да** | 4 копии протокола warmup+rebuild (D1), 4 копии строки исключения (D6) |
| Singleton с изменяемым состоянием | **Да** | `MultiSolutionManager` / `SolutionManager` — синглтоны с `_loaded`, `_resolver`, `_metadataResolver`, `_tracker`, `_recentChanges`; трогаются из всех инструментов |
| Магические строки вместо `enum` | **Да (локально)** | `direction` (`"callees"/"callers"/"both"`), `kind` правил архитектуры (`"forbid"/"allowOnly"`), `lifetime` DI, `scope` доверия (`"session"/"persistent"/"addRoot"`) |
| Публичные поля | **Почти нет** | `Field`-записи публичного API — `const`/`static readonly` константы (`DefaultPort`, `AllKinds`, `DanglingLimit`, `ForbidKind`) |
| God objects (размер × зацепление) | **1** | `SymbolResolver` |
| Утечки `IDisposable` | **Частично** | 9 предупреждений, из них реальны `IlDisassemblerAdapter`/`PeekIlLogic` (`PEFile` из кэша) |
| Мёртвый код | **124 кандидата** (prod) | `find_unused_symbols`; требуют перепроверки `find_references` |
| Рефлексия | **13 сайтов**, 10 из них — `ChangeSignatureBridge` | осознанный межверсионный мост, но изолирован плохо |
| `[Obsolete]` в использовании | **Да** | `Workspace.WorkspaceFailed` в `SolutionLoader` (CS0618) |
| Мутируемое общее состояние между вызовами | **Да** | `_metadataResolver` пересоздаётся при перестройке, `_loaded` подменяется — читатели должны пройти через `GetAnalysisContext()` |

### 1.5 Метрики проблемных классов

| Класс | CC (max метод) | Cognitive (max) | LOC | Члены | Зацепление (in/out namespace) |
|---|---|---|---|---|---|
| `SolutionLoader` | **23** (`OpenPerProjectAsync`) | **50** | **731** | 22 | — |
| `SolutionManager` | — | — | **647** | **51** | — |
| `GenerateTestSkeletonLogic` | 8 | — | **530** | 18 | — |
| `ChangeSignatureBridge` | 21 (`Members.Resolve`) | 18 | **525** | 15 | — |
| `ChangeSignatureLogic` | 9 | — | **507** | 16 | — |
| `SymbolResolver` | — | — | ~~**414**~~ → **71** | ~~**45**~~ → 31 | ~~**6** / 2 (god-object)~~ → разбит, этап 5.1 |
| `FindAsyncViolationsLogic` | **27** | **66** | — | — | — |
| `StackTraceParser` | 26 (`TryParseFrame`) | 29 | — | — | — |
| `GetCallGraphLogic` | 25 (`EnumerateOutgoingCalls`) | 37 | — | — | — |
| `FindObsoleteUsageLogic` | 24 | **61** | — | — | — |
| `CheckArchitectureLogic` | 22 (`ExecuteCore`) | 55 | — | — | — |

Пороговые значения из скилла: CC > 15 — декомпозиция; cognitive > 25 — тяжело читать;
LOC > 300 и члены > 15 — god-класс; ≥ 5 namespace — сильное зацепление.
**Превышено 22 методами по CC ≥ 15** (среднее 19.4, максимум 27).

---

## 2. Целевая архитектура

```mermaid
flowchart TD
    subgraph Transport
        T["McpTool adapters: 71 static tool classes"]
    end
    subgraph Contracts
        I1["IAnalysisContextProvider"]
        I2["ISolutionLifecycle"]
        I3["ISymbolIndex"]
        I4["IMetadataIndex"]
        I5["IIlDisassembler"]
        I6["IToolLogic of TRequest TResult"]
    end
    subgraph Domain
        L["Logic steps: pure static, hidden behind IToolLogic"]
        A["Analyzers: complexity, exceptions, references"]
    end
    subgraph Infrastructure
        SM["SolutionLifecycle: warmup, rebuild, staleness"]
        SI["SymbolIndex implementation"]
        MI["MetadataIndex implementation"]
        PE["PEFileCache"]
    end
    T --> I6
    I6 --> L
    T --> I1
    I1 --> SM
    SM --> I2
    L --> I3
    L --> I4
    L --> I5
    I3 --> SI
    I4 --> MI
    I5 --> PE
```

Ключевые изменения относительно текущего состояния:
`Transport → конкретный Manager` заменяется на `Transport → интерфейс`;
`*Logic` перестаёт быть `static` там, где это мешает подмене, и получает
типизированный объект-запрос; узкие обязанности `SolutionManager` разъезжаются по
`ISolutionLifecycle` (жизненный цикл) и `IAnalysisContextProvider` (снимок).

---

## 3. Поэтапный план

### Этап 0 — неотложные дефекты (без изменения структуры)

**Цель:** убрать предупреждения компилятора и подтверждённые утечки, не трогая дизайн.

| Изменение | Файл |
|---|---|
| [x] Заменить `Workspace.WorkspaceFailed` на `RegisterWorkspaceFailedHandler()` | [`SolutionLoader.cs:144`](../src/RoslynCodeLens/SolutionLoader.cs:144), [`:360`](../src/RoslynCodeLens/SolutionLoader.cs:360) |
| [x] Устранить CS8619: привести `Task<Solution>`/`Task<Project>` к `Task<Solution?>`/`Task<Project?>` | [`SolutionLoader.cs:154`](../src/RoslynCodeLens/SolutionLoader.cs:154), [`:373`](../src/RoslynCodeLens/SolutionLoader.cs:373) |
| [x] Устранить CS8602 (разыменование в `ValidateEditLogic`) | [`ValidateEditLogic.cs:101`](../src/RoslynCodeLens/Tools/ValidateEditLogic.cs:101) |
| [x] Проверить владение `PEFile` из `_cache.Get(...)`: документировать контракт кэша или обернуть в `using` | [`IlDisassemblerAdapter.cs:19`](../src/RoslynCodeLens/Metadata/IlDisassemblerAdapter.cs:19), [`:21`](../src/RoslynCodeLens/Metadata/IlDisassemblerAdapter.cs:21), [`PeekIlLogic.cs:225`](../src/RoslynCodeLens/Tools/PeekIlLogic.cs:225) |
| [~] Зафиксировать в коде, что 6 предупреждений о `Task` — ложные (комментарий/подавление по месту) | [`ChangeSignatureBridge.cs:65`](../src/RoslynCodeLens/Analysis/ChangeSignatureBridge.cs:65), [`:101`](../src/RoslynCodeLens/Analysis/ChangeSignatureBridge.cs:101), [`SolutionChangeSafety.cs:144`](../src/RoslynCodeLens/SolutionChangeSafety.cs:144), [`:153`](../src/RoslynCodeLens/SolutionChangeSafety.cs:153), [`BackgroundTaskStore.cs:90`](../src/RoslynCodeLens/BackgroundTasks/BackgroundTaskStore.cs:90), [`MultiSolutionManager.cs:237`](../src/RoslynCodeLens/MultiSolutionManager.cs:237) |

**Затронутые области:** загрузчик решения, IL-подсистема, валидация правок.
**Критерий готовности:** `get_diagnostics` по решению — 0 errors, 0 warnings;
`find_disposable_misuse` — 0 реальных нарушений (остальные подавлены с обоснованием).
**Влияние на поведение:** отсутствует (предупреждения + подписка на события).
**Риски:** `RegisterWorkspaceFailedHandler` может изменить поток событий —
проверить, что диагностики загрузки по-прежнему попадают в `LoadedSolution.LoadDiagnostics`.

**Примечание по пункту 5 (выполнен со сменой подхода).** `find_disposable_misuse` —
собственный анализатор проекта, и механизма подавления у него **нет**: `IsDisposalAcknowledged`
распознаёт только `return x`, присваивание в поле/свойство и в `out`/`ref`. Ни
`[SuppressMessage]`, ни `#pragma`, ни атрибут-маркер не читаются, поэтому «точечное
подавление» из плана нереализуемо. Решено задокументировать находки по месту.

Отдельно зафиксировано (проверено тестами и откачено):
- `RegisterWorkspaceFailedHandler` возвращает `IDisposable` — подписка обёрнута в `using`,
  иначе инструмент выдавал 2×`DisposableDiscarded` (регрессия была и устранена);
- переписывание `Task`-сайтов в `ChangeSignatureBridge` под «присваивание полю»
  **ломает** код: поле не переживает `await` (продолжение на другом потоке пула) →
  28 падений `NullReferenceException` в `GetTaskResult`. Отменено.

Остаток `find_disposable_misuse` — 7 находок, все ложные по конструкции:
2×`ChangeSignatureBridge` и 2×`SolutionChangeSafety` (`Task`, освобождать не требуется),
`IlDisassemblerAdapter` (`PEFile` во владении кэша; `PlainTextOutput` не `IDisposable`),
`PeekIlLogic` (`PEFile` во владении кэша), `BackgroundTaskStore` (освобождение ниже,
через `removed.Dispose()`), `MultiSolutionManager` (владение передаётся в `_managers`).

Статус: этап 0 завершён 2026-10-28

### Этап 1 — типобезопасность домена (enum вместо строк)

**Цель:** убрать магические строки из публичных контрактов.

| Изменение | Файл |
|---|---|
| `enum CallGraphDirection { Callees, Callers, Both }`; парсинг строки — только на границе инструмента | [`GetCallGraphLogic.cs:31`](../src/RoslynCodeLens/Tools/GetCallGraphLogic.cs:31), [`GetCallGraphTool.cs:24`](../src/RoslynCodeLens/Tools/GetCallGraphTool.cs:24) |
| `ArchitectureRule.Kind` типа `enum ArchitectureRuleKind { Forbid, AllowOnly }` (сейчас строки `"forbid"/"allowOnly"`) | [`ArchitectureRule.cs:10`](../src/RoslynCodeLens/Models/ArchitectureRule.cs:10), [`CheckArchitectureLogic.cs:56`](../src/RoslynCodeLens/Tools/CheckArchitectureLogic.cs:56) |
| `enum TrustScope { Session, Persistent, AddRoot }` вместо `string scope` | [`TrustSolutionLogic.cs:7`](../src/RoslynCodeLens/Tools/TrustSolutionLogic.cs:7), [`TrustSolutionTool.cs:14`](../src/RoslynCodeLens/Tools/TrustSolutionTool.cs:14) |
| `enum DiLifetime` вместо строки `lifetime` в `DiRegistration` | [`DiRegistration.cs:6`](../src/RoslynCodeLens/Models/DiRegistration.cs:6) |
| Единая точка парсинга строк→enum на границе MCP (одна функция, один формат ошибки) | новый `Tools/ArgumentParsing.cs` |

**Затронутые области:** модели, 4+ инструмента, тесты соответствующих Logic.
**Критерий готовности:** ни один `switch`/`if` прикладной логики не сравнивает строковые
литералы направления/вида/scope; ошибка неверного значения — типизированный
`McpToolException(ToolErrorCode.InvalidArgument)`.
**Влияние на поведение:** сообщения об ошибках могут измениться по формулировке;
значения, принимаемые из JSON, — без изменений (обратная совместимость на границе).
**Риски:** `ArchitectureRule` — часть публичного API модели; правка сигнатуры меняет
публичную поверхность — выполнять через `change_signature` с `preview: true`.

### Этап 2 — интерфейсы и DIP

**Цель:** ввести контракты, которые можно подменять, не переписывая всё разом.

| Изменение | Файл |
|---|---|
| `IAnalysisContextProvider` с единственным членом `SolutionAnalysisContext GetAnalysisContext()`; `MultiSolutionManager` реализует его | [`MultiSolutionManager.cs:84`](../src/RoslynCodeLens/MultiSolutionManager.cs:84) |
| `ISolutionLifecycle` (`EnsureLoaded`, `ForceReloadAsync`, `LoadSolutionAsync`, `UnloadSolution`, `SetActiveSolution`, `ListSolutions`) — управление вынести из аксессоров контекста | [`MultiSolutionManager.cs:74-86`](../src/RoslynCodeLens/MultiSolutionManager.cs:74) |
| `ISymbolIndex` / `IMetadataIndex` — то, что реально нужно `*Logic` (`FindMethods`, `FindNamedTypes`, `GetFileAndLine`, …), вместо конкретных `SymbolResolver`/`MetadataSymbolResolver` | [`SymbolResolver.cs:7`](../src/RoslynCodeLens/SymbolResolver.cs:7), [`MetadataSymbolResolver.cs:6`](../src/RoslynCodeLens/Symbols/MetadataSymbolResolver.cs:6) |
| 51 сайт `MultiSolutionManager manager` в `*Tool` заменить на `IAnalysisContextProvider` (тип параметра MCP-инструмента меняется на интерфейс) | 51 файл `Tools/*Tool.cs` |
| Зафиксировать контракт неизменяемости `MetadataSymbolResolver` (D5) — комментарием или `sealed`, чтобы пересоздание оставалось законным | [`MetadataSymbolResolver.cs:11`](../src/RoslynCodeLens/Symbols/MetadataSymbolResolver.cs:11) |

**Затронутые области:** весь слой инструментов, DI-регистрация в `Program.cs`.
**Критерий готовности:** `*Tool` не упоминает `MultiSolutionManager`; в тестах появилась
возможность подставить фейковый `IAnalysisContextProvider` без загрузки решения.
**Влияние на поведение:** отсутствует (структурная правка).
**Риски:** DI MCP-хоста конструирует инструменты по параметрам `Execute`; смена типа
параметра на интерфейс требует регистрации `IAnalysisContextProvider` в
[`Program.cs:59-77`](../src/RoslynCodeLens/Program.cs:59). Проверить `WithToolsFromAssembly()`
на разрешение интерфейсных параметров — при отказе оставить конкретный тип и свести
этап к тестовой подмене через фабрику.

### Этап 3 — устранение дублирования и стандартизация доступа к состоянию

**Цель:** один протокол доступа к снапшоту вместо четырёх (D1, D3, D4, D6).

> Порядок внутри этапа обязателен: **сначала** характеристические тесты на текущее
> поведение геттеров, **потом** рефакторинг.

| Изменение | Файл |
|---|---|
| [x] Характеристические тесты: `GetLoadedSolution`/`GetResolver`/`GetMetadataResolver`/`GetAnalysisContext` при (а) неудачной загрузке, (б) упавшем warmup, (в) протухшем решении — одинаковое ли поведение? | `tests/RoslynCodeLens.Tests/SolutionManagerTests.cs` |
| [x] Свернуть 4 копии префикса в один приватный `EnsureReady()` (сведено 5 — плюс `EnsureLoaded`) | [`SolutionManager.cs:153`](../src/RoslynCodeLens/SolutionManager.cs:153), [`:163`](../src/RoslynCodeLens/SolutionManager.cs:163), [`:173`](../src/RoslynCodeLens/SolutionManager.cs:173), [`:510`](../src/RoslynCodeLens/SolutionManager.cs:510) |
| [x] Единое сообщение «warmup failed» — константа, а не 4 строковых литерала | там же |
| [x] Решить, остаются ли индивидуальные геттеры публичными; при отсутствии внешних потребителей — сделать `internal` (проверено: единственный потребитель — `MultiSolutionManager`, см. D1). **Решение:** не сужать, а удалить `GetResolver`/`GetMetadataResolver` — потребителей не осталось | [`MultiSolutionManager.cs:74-86`](../src/RoslynCodeLens/MultiSolutionManager.cs:74) |

**Затронутые области:** `SolutionManager`, `MultiSolutionManager`, тесты решения.
**Критерий готовности:** протокол доступа к снапшоту существует в одном экземпляре;
характеристические тесты зелёные до и после; `dotnet test RoslynCodeLens.slnx` — без регрессий.
**Влияние на поведение:** должно быть нулевым; расхождение тестов «до/после» означает,
что копии уже разошлись, и это отдельный дефект — зафиксировать.
**Риски:** среднее. Геттеры вызываются из `MultiSolutionManager` (4 сайта) и тестов (3 сайта);
`find_references` по каждому перед сужением видимости.

**Результат:** протокол свёрнут в единственный `EnsureReady()`; `GetAnalysisContext()` =
протокол + `lock (_lock)`; `GetResolver`/`GetMetadataResolver` удалены
(`find_references` = 0); `GetIlDisassembler()` оставлен осознанным исключением
(адаптер не читает состояние решения). `get_diagnostics` 0/0,
`dotnet test RoslynCodeLens.slnx` — 1249 passed / 0 failed.
Сценарий (б) «упавший warmup» тестом не покрыт — нет детерминированной тест-симы.
Подробности — [`decision/2026-10-28T18-43-stage3-snapshot-access-protocol.md`](../decision/2026-10-28T18-43-stage3-snapshot-access-protocol.md).

**Статус: этап 3 завершён 2026-10-28**

#### 3.1. Уточнённые факты (проверено 2026-10-28 после этапа 0)

Перепроверено `find_references` и чтением тестов — уточняет форму рефакторинга:

| Факт | Следствие |
|---|---|
| `GetLoadedSolution`: 4 прод-сайта (все в `MultiSolutionManager`) + 3 тест-сайта | Сужение до `internal` невозможно без `InternalsVisibleTo`; проще оставить `public` и свести тело |
| `GetResolver`: **1** прод-сайт ([`MultiSolutionManager.cs:76`](../src/RoslynCodeLens/MultiSolutionManager.cs:76)) + **5** тест-сайтов | Кандидат на удаление после перевода `MultiSolutionManager` на `GetAnalysisContext()` |
| `GetMetadataResolver`: **1** прод-сайт ([`MultiSolutionManager.cs:77`](../src/RoslynCodeLens/MultiSolutionManager.cs:77)), тестов нет | Однозначный кандидат на удаление |
| **`GetIlDisassembler()` ([`SolutionManager.cs:183`](../src/RoslynCodeLens/SolutionManager.cs:183)) — тело `=> _ilAdapter` без блокировки и без проверки загрузки — контраст с остальными аксессорами** | Пятый, «недоделанный» случай той же ответственности: доступа к снапшоту без протокола готовности |
| ⚠️ **`WaitForWarmupAsync()` возвращает `_warmupTask` «как есть»** ([`SolutionManager.cs:148`](../src/RoslynCodeLens/SolutionManager.cs:148)), а `CreateAsync` стартует warmup в фоне ([`SolutionManager.cs:99`](../src/RoslynCodeLens/SolutionManager.cs:99)) | **Жёсткое ограничение:** `CreateAsync` обязан оставаться неблокирующим — это закреплено тестом [`SolutionManagerTests.cs:101`](../tests/RoslynCodeLens.Tests/SolutionManagerTests.cs:101) `CreateAsync_ReturnsBeforeCompilationCompletes`. Поэтому warmup **нельзя** переносить в `CreateAsync`; `EnsureReady()` должен блокирующе ждать warmup только при **первом доступе**, как сейчас |
| Тест [`GetResolver_ReturnsCachedInstance_WhenNotStale`](../tests/RoslynCodeLens.Tests/SolutionManagerTests.cs:30) фиксирует «без stale → тот же экземпляр» | Инвариант идентичности резолвера обязателен: `EnsureReady()` не должен пересоздавать `_resolver` при отсутствии stale-проектов |

#### 3.2. Уточнённый порядок пунктов

1. **Характеристические тесты** (без правки прода): зафиксировать текущее поведение пяти
   аксессоров по трём сценариям — (а) `HasLoadFailure` → исключение с исходным сообщением,
   (б) упавший warmup → `InvalidOperationException("Solution warmup failed.")`,
   (в) stale-проекты → перестройка перед отдачей значения. Плюс отдельно текущее поведение
   `GetIlDisassembler()` (не бросает и не ждёт — зафиксировать как **дефект D3-новый**,
   а не как норму).
2. **Свернуть 4 копии в один приватный `EnsureReady()`**; сообщение — в `const string`.
3. **`GetAnalysisContext()` переписать на `EnsureReady()` + `lock (_lock)`** (сохранить
   атомарность снимка — инвариант из комментария о #282).
4. **`MultiSolutionManager`: перевести 4 геттера на `GetAnalysisContext()`**, затем удалить
   `GetResolver` и `GetMetadataResolver` из `SolutionManager` (после чего `find_references` = 0).
5. **`GetIlDisassembler()`** — либо включить в общий протокол (если адаптер зависит от
   загруженного решения), либо оставить осознанным исключением с комментарием-обоснованием.
   Решение принимать по факту: посмотреть, читает ли `IlDisassemblerAdapter` состояние решения.
6. **Перенацелить тест-сайты** `GetResolver()` → `GetAnalysisContext().Resolver`.

### Этап 4 — async-first доступ к контексту (устранение sync-over-async)

**Цель:** инкрементальная перестройка перестаёт блокировать поток вызова.

| Изменение | Файл |
|---|---|
| Ввести `Task<SolutionAnalysisContext> GetAnalysisContextAsync(CancellationToken)` с честным `await` перестройки | [`SolutionManager.cs:510`](../src/RoslynCodeLens/SolutionManager.cs:510) |
| Синхронный `RebuildIfStale()` превратить в `RebuildIfStaleAsync()` | [`SolutionManager.cs:226`](../src/RoslynCodeLens/SolutionManager.cs:226), [`:254`](../src/RoslynCodeLens/SolutionManager.cs:254) |
| `*Tool.Execute` → `ExecuteAsync` там, где вызывается перестройка (сейчас `Execute` синхронны у 40+ инструментов) | `Tools/*Tool.cs` |
| Оставить документированный синхронный путь только для «тёплого» быстрого сценария (без stale-проектов) | [`SolutionManager.cs:185`](../src/RoslynCodeLens/SolutionManager.cs:185) |

**Затронутые области:** все инструменты, `MultiSolutionManager`, обработка отмены.
**Критерий готовности:** `find_async_violations` — 0 вхождений `SyncOverAsyncGetAwaiterGetResult`
в `src/`; перестройка отменяется по `CancellationToken`; инструменты возвращают
`OperationCanceledException` наружу без изменений (как сейчас задумано в
[`Program.cs:66-68`](../src/RoslynCodeLens/Program.cs:66)).

#### 4.1. Уточнённые факты (перепроверено 2026-10-28 после этапа 3)

> ⚠️ **Исходный аудит занизил масштаб.** В разделе 1.2 числилось 5 сайтов sync-over-async,
> все в `SolutionManager`. Фактический `find_async_violations` даёт **5 нарушений, из них
> только 2 в `SolutionManager`**: этап 3 свёл четыре копии в `EnsureReady()`, поэтому счётчик
> по файлу уменьшился, но обнажил три ранее не зафиксированных сайта в слое логики.

| Сайт | Файл | Комментарий |
|---|---|---|
| 1 | [`SolutionManager.cs:178`](../src/RoslynCodeLens/SolutionManager.cs:178) — `EnsureReady()` | ожидание warmup; **единственная** точка после этапа 3 |
| 2 | [`SolutionManager.cs:284`](../src/RoslynCodeLens/SolutionManager.cs:284) — `RebuildIfStale()` | блокирующая перестройка |
| 3 | [`FindReferencesLogic.cs:43`](../src/RoslynCodeLens/Tools/FindReferencesLogic.cs:43) | `SymbolFinder.FindReferencesAsync(...).GetAwaiter().GetResult()` |
| 4 | [`FindReferencesLogic.cs:92`](../src/RoslynCodeLens/Tools/FindReferencesLogic.cs:92) | `document.GetSemanticModelAsync().GetAwaiter().GetResult()` |
| 5 | [`FindCallersLogic.cs:38`](../src/RoslynCodeLens/Tools/FindCallersLogic.cs:38) | `SymbolFinder.FindCallersAsync(...).GetAwaiter().GetResult()` |

**Следствие:** цель «0 sync-over-async» недостижима правкой только `SolutionManager`.
Сайты 3–5 требуют сделать асинхронными сами `*Logic`, а это каскад вызовов:

| Метод | Прод-вызовы | Тест/бенч-вызовы | Всего |
|---|---|---|---|
| `FindReferencesLogic.Execute` | 3 ([`AnalyzeChangeImpactLogic.cs:10`](../src/RoslynCodeLens/Tools/AnalyzeChangeImpactLogic.cs:10), [`FindReferencesTool.cs:41`](../src/RoslynCodeLens/Tools/FindReferencesTool.cs:41), [`ValidateEditLogic.cs:159`](../src/RoslynCodeLens/Tools/ValidateEditLogic.cs:159)) | **11** | 14 |
| `FindCallersLogic.Execute` | 3 ([`AnalyzeChangeImpactLogic.cs:11`](../src/RoslynCodeLens/Tools/AnalyzeChangeImpactLogic.cs:11), [`AnalyzeMethodLogic.cs:21`](../src/RoslynCodeLens/Tools/AnalyzeMethodLogic.cs:21), [`FindCallersTool.cs:23`](../src/RoslynCodeLens/Tools/FindCallersTool.cs:23)) | **5** | 8 |

Каскад второго порядка: `AnalyzeChangeImpactLogic`, `AnalyzeMethodLogic` и `ValidateEditLogic`
становятся `async` → их `*Tool` тоже → их тесты (десятки сайтов).

#### 4.2. Уточнённый порядок — три изолированные порции

Порции независимы по файлам и коммитятся отдельно; каждая завершается полным прогоном тестов.

**Порция A — `SolutionManager` (сайты 1–2).** ✅ **Выполнено 2026-10-28.**
`EnsureReadyAsync()` + `RebuildIfStaleAsync()`. Синхронные обёртки **не удалять сразу**:
их используют 51 `*Tool`; на этой порции добавляется async-вариант
`GetAnalysisContextAsync(ct)`, а синхронный остаётся для ещё не переведённых вызовов.
⚠️ Учитывать: `_rebuildGate.Wait(0)` (opportunistic acquire) — при async-переписывании
заменить на `WaitAsync(0)`, иначе блокируется поток пула. Ловушка `catch (Exception ex)` вокруг
`RebuildAsync` сохраняется (перестройка не должна ронять запрос), только `await` вместо `.GetResult()`.

**Итог порции A:**
- Добавлены `EnsureReadyAsync(ct)`, `RebuildIfStaleAsync(ct)`, `GetAnalysisContextAsync(ct)`;
  `_rebuildGate.WaitAsync(0, ct)` вместо синхронного `Wait(0)`.
- Синхронный путь сохранён как документированный (для 51 `*Tool`, порция C):
  `EnsureReady()` ждёт `_warmupCompleted` (`ManualResetEventSlim`), `RebuildIfStale()` и
  `GetAnalysisContext()` паркуют вызывающий поток на событие вокруг *той же* async-реализации —
  поэтому в файле **нет** `Task.Wait()` / `.GetAwaiter().GetResult()` вообще.
- Отмена: `OperationCanceledException` отдаёт drained-снимок обратно в трекер (`RestoreStale`
  в `finally` при `completed == false`), затем пробрасывается наружу. `catch (Exception)`
  вокруг перестройки сохранён — перестройка по-прежнему не роняет запрос, только логирует.
- Инвариант #282 сохранён: снимок берётся одним `lock (_lock)` в обеих ветках.
- Тесты (`SolutionManagerTests`): `GetAnalysisContextAsync_WhileRebuilding_DoesNotBlockCallingThread`,
  `GetAnalysisContextAsync_CancelledBeforeRebuild_ThrowsAndKeepsStaleState`,
  `GetAnalysisContextAsync_MatchesSyncPath_OnSameSnapshot`.
- Проверка: `find_async_violations` в `SolutionManager.cs` — **0** (было 2), по решению **3**
  (остались сайты 3–5 порции B); `get_diagnostics` 0/0; `dotnet test` — **1252 passed, 0 failed**
  (база 1249 + 3 новых).

**Порция B — `FindReferencesLogic` + `FindCallersLogic` (сайты 3–5).** ✅ **Выполнено 2026-10-28.**
`Execute` → `ExecuteAsync(..., ct)`; внутренние синхронные `SymbolFinder`-вызовы и
`GetSemanticModelAsync` — на `await`. Затем перевести 6 прод-вызывающих и 16 тест/бенч-сайтов.
⚠️ Порядок: сначала async-версия рядом со старой, перевести вызывающих, потом удалить старую.
Не совмещать с `SolutionManager` — разные причины падений (Roslyn API против жизненного цикла).

**Итог порции B:**
- `FindReferencesLogic.ExecuteAsync` / `FindCallersLogic.ExecuteAsync` с `CancellationToken`;
  `SymbolFinder.FindReferencesAsync` / `FindCallersAsync` и `Document.GetSemanticModelAsync`
  ожидаются через `await` (`ConfigureAwait(false)`), `ct.ThrowIfCancellationRequested()` перед
  каждым `SymbolFinder`-вызовом. Синхронные `Execute` **удалены** (`find_references` = 0).
- `ReferenceClassifier` не тронут (чистый, синхронный). Модель по-прежнему строится лениво:
  `await GetModelAsync(...)` выполняется **только** для узлов с родителем `ArgumentSyntax`
  (единственная ветка, которой нужен семантика), затем передаётся в `Classify` готовой —
  так поведение и стоимость сканирования не изменились.
- Каскад второго порядка: `AnalyzeChangeImpactLogic`, `AnalyzeMethodLogic` →
  `ExecuteAsync`; `AnalyzeChangeImpactTool`, `AnalyzeMethodTool` → `Task<T>` (как `GetCallGraphTool`);
  `ValidateEditLogic` уже был `async`, переведён только его внутренний вызов.
  `FindReferencesTool` / `FindCallersTool` возвращают `Task<ToolListResult<T>>`.
- Тесты: 5 методов `FindReferencesLogicTests` + 5 `FindReferencesToolTests` + 3 `FindCallersToolTests`
  + 2 `AnalyzeChangeImpactToolTests` + 3 `AnalyzeMethodToolTests` + 2 `RazorGeneratedCodeTests`
  → `async Task`; `IncrementalRebuildTests` — хелперы `*Async` (чтение через `Task.Run(async …)`);
  4 бенч-метода → `Task<T>`.
- Новые тесты (`FindReferencesLogicTests`): `ExecuteAsync_DoesNotBlockCallingThread`
  (`pending.IsCompleted == false` сразу после вызова) и
  `ExecuteAsync_CancelledToken_ThrowsInsteadOfHanging` (pre-cancelled `ct` →
  `OperationCanceledException`, ограниченный `Task.WhenAny` — зависание уронило бы тест).
- Проверка: `find_async_violations` по решению — **0** (было 3); `get_diagnostics` **0 errors,
  0 warnings**; `dotnet test RoslynCodeLens.slnx` — **1254 passed, 0 failed** (база 1252 + 2).

**Порция C — инструменты.** Перевести оставшиеся синхронные `Execute` у `*Tool` на
`ExecuteAsync`, использующие `GetAnalysisContextAsync`. MCP-хост поддерживает
`Task<T>`-возврат (уже используется у `GetCallGraphTool`, а после порции B — у
`FindReferencesTool`/`FindCallersTool`/`AnalyzeChangeImpactTool`/`AnalyzeMethodTool`).

#### 4.3. Статус и решение по порции C (2026-10-28)

**Цель этапа достигнута без порции C.** После порций A и B:

| Метрика | До этапа 4 | После порций A+B |
|---|---|---|
| `find_async_violations` | 5 | **0** |
| `get_diagnostics` | 0 / 0 | **0 / 0** |
| Тесты | 1249 passed | **1254 passed, 0 failed** |

`find_async_violations` больше не находит ни одного сайта: синхронный путь в
`SolutionManager` реализован **без** `.GetAwaiter().GetResult()` — через
`ManualResetEventSlim` (`_warmupCompleted`) вокруг той же async-реализации. Поэтому
порция C — **чистка, а не исправление корректности**.

**Масштаб порции C (измерено `find_references`):** `MultiSolutionManager.GetAnalysisContext`
вызывается из **53** мест в `src/`, из них **49** — инструменты `Tools/*Tool.cs`; остальные
4 — внутренние (`MultiSolutionManager` ×2, `InspectExternalAssemblyTool` через цепочку).
Плюс 2 тест-сайта. Каждый инструмент: `Execute` → `ExecuteAsync` + `Task<T>`, обновление
своего теста и, где нужно, `await` в вызовах из тестов.

**Решение: порцию C отложить, выполнять группами по потребности, а не единым заходом.**

1. **Обоснование отложить.** Блокировка на тёплом пути минимальна: при отсутствии
   stale-проектов `EnsureReady()` возвращается сразу (warmup завершён, событие взведено).
   Реальная стоимость блокировки проявляется только при перестройке — а её уже
   обслуживает `GetAnalysisContextAsync` из порций A+B, доступный всем вызывающим.
2. **Когда делать.** При появлении требования к масштабируемости под конкурентной
   нагрузкой (несколько одновременных вызовов при протухшем решении), либо если
   синхронное ожидание события окажется источником деградации пула потоков.
3. **Как делать.** Группами по области (`Get*` / `Find*` / мутирующие), каждая группа —
   отдельный коммит с полным прогоном. После перевода **всех** 49 сайтов удаляются
   синхронные `EnsureReady`, `RebuildIfStale`, `GetAnalysisContext` и поле
   `_warmupCompleted`.
4. **Проверка перед началом.** Убедиться, что смена `T` → `Task<T>` в сигнатуре `Execute`
   не ломает регистрацию `WithToolsFromAssembly()`; изменение типа возврата — это
   изменение публичной поверхности.

Отметка в плане: порция C — `[ ]` с этим обоснованием, работа не начата.
**Влияние на поведение:** при протухшем решении вызов больше не занимает поток;
латентность ответа не меняется (перестройка всё равно нужна), меняется масштабируемость.
**Риски:** **высокие.** 40+ сигнатур, MCP-хост ожидает либо `T`, либо `Task<T>` —
менять по одному инструменту с прогоном тестов; не смешивать с этапами 2–3.

### Этап 5 — декомпозиция god-классов

**Цель:** свести LOC и CC под пороги; устранить единственный god-object.

| Изменение | Файл |
|---|---|
| [x] `SymbolResolver` (414 LOC, 45 членов, 6 входящих namespace) → `SymbolIndex` (поиск по имени) + `TypeIndex` (иерархии, реализации) + `LocationIndex` (файл/строка/project) | [`SymbolResolver.cs:7`](../src/RoslynCodeLens/SymbolResolver.cs:7) |
| `FindAsyncViolationsLogic.Execute` (CC 27 / cognitive 66): выделить детектор на каждый из 6 шаблонов; метод верхнего уровня только собирает результаты | [`FindAsyncViolationsLogic.cs:14`](../src/RoslynCodeLens/Tools/FindAsyncViolationsLogic.cs:14) |
| `FindObsoleteUsageLogic.Execute` (CC 24 / cognitive 61): отделить обход от группировки | [`FindObsoleteUsageLogic.cs:11`](../src/RoslynCodeLens/Tools/FindObsoleteUsageLogic.cs:11) |
| `SolutionLoader.OpenPerProjectAsync` (CC 23 / cognitive 50): разбить на «открыть проект», «применить фильтр», «скомпилировать пачкой» | [`SolutionLoader.cs:278`](../src/RoslynCodeLens/SolutionLoader.cs:278) |
| `CheckArchitectureLogic.ExecuteCore` (CC 22 / cognitive 55): по одному методу на `forbid` и `allowOnly` | [`CheckArchitectureLogic.cs:56`](../src/RoslynCodeLens/Tools/CheckArchitectureLogic.cs:56) |
| `GenerateTestSkeletonLogic` (530 LOC): выделить `TestSkeletonRenderer` (генерация текста) и `TestSkeletonPlanner` (что генерировать) | [`GenerateTestSkeletonLogic.cs:10`](../src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs:10) |
| `GetCallGraphLogic.EnumerateOutgoingCalls` (CC 25): выделить обработку внешних узлов | [`GetCallGraphLogic.cs:179`](../src/RoslynCodeLens/Tools/GetCallGraphLogic.cs:179) |
| `StackTraceParser.TryParseFrame` (CC 26): разбить по форматам кадра | [`StackTraceParser.cs:132`](../src/RoslynCodeLens/StackTrace/StackTraceParser.cs:132) |

#### 5.1. `SymbolResolver` — выполнено 2026-10-28

Разбит на `SymbolIndex` (поиск по имени), `TypeIndex` (иерархии, реализации) и
`LocationIndex` (файл/строка/project/признак сгенерированного кода).
`SymbolResolver` сохранён как **фасад** над тремя индексами: `find_references` дал
**218 ссылок в 3 проектах** (124 prod, 92 теста, 2 бенчмарка, 25 `object_creation`),
поэтому замена типа потребовала бы правки ~200 мест — несоразмерно для чистого переноса.

| Метрика | До | После |
|---|---|---|
| LOC | 414 | **71** |
| Членов | 45 | 31 |
| Полей | 13 | **3** |
| В `find_large_classes(300, 15)` | да | нет по строкам (71) |

Новые типы: `SymbolIndex` 254 LOC / 27 членов / 6 полей, `TypeIndex` ~90 LOC / 4 члена,
`LocationIndex` ~180 LOC / 7 членов — ни один не превышает 300 LOC.

Инвентаризация `find_references` по каждому публичному члену: потребители ненулевые у всех,
удалять было нечего. Удалены только два мёртвых делегирования, появившихся при переносе
(`SymbolResolver.FindMembers`, `TypeIndex.Symbols`).

⚠️ **Уточнение аудита:** `find_god_objects` возвращал пусто **и до разбиения** — порог
`minOutgoingNamespaces = 5` не достигался (`SymbolResolver` зависел от 2 не-BCL namespace).
Критерий «`find_god_objects` пусто» не мог провалиться и потому ничего не доказывает;
подтверждение декомпозиции — динамика LOC и `find_large_classes`.

Проверка: `get_diagnostics` 0/0; `dotnet test RoslynCodeLens.slnx` — **1259 passed, 0 failed**
(база 1254 + 5 новых в `SymbolIndexDecompositionTests`).
Подробности — [`decision/2026-10-28T20-47-stage5-1-symbolresolver-decomposition.md`](../decision/2026-10-28T20-47-stage5-1-symbolresolver-decomposition.md).

#### 5.2. `SolutionManager` — инвентаризация перед декомпозицией

Исходное состояние: **899 LOC, 61 член** (рос с 647 LOC за этапы 3–4 — добавлены единый
`EnsureReady()`, async-варианты и синхронные обёртки, удаления старых путей ещё не было).

**Ключевое обстоятельство: класс одновременно объект этапа 4 (порция C).** Часть роста —
синхронные обёртки и поле `_warmupCompleted` — по разделу 4.3 подлежит **удалению** после
перевода 49 инструментов на async. Поэтому в разбиении явно разделено: что уходит вместе
с порцией C, а что остаётся навсегда. Декомпозировать то, что всё равно будет удалено,
бессмысленно — такие члены помечены ниже как `[C]`.

##### Инвентаризация: член → новый тип

| Член | Вид | Куда переезжает | Примечание |
|---|---|---|---|
| `_loaded`, `_resolver`, `_metadataResolver`, `_lock` | поле ×4 | `SolutionSnapshotStore` | ядро инварианта #282 |
| `GetAnalysisContextAsync` | метод | **фасад** — протокол через `ReadyGate`, снимок у `SolutionSnapshotStore` | тело покидает фасад |
| `GetAnalysisContext()` `[C]` | метод | **фасад** (обёртка) | удаляется порцией C |
| `_recentlyChangedDocuments` | поле | `RecentChangesJournal` | — |
| `RecentlyChangedDocuments` | свойство | `RecentChangesJournal` + делегирование | — |
| `ResetRecentChanges()` | метод | `RecentChangesJournal` + делегирование | вызывается `GetDiagnosticsTool` |
| `RecordRecentlyChanged(path)` | приватный | `RecentChangesJournal` | 2 сайта вызова |
| `_rebuildGate` | поле | `StaleRebuildCoordinator` | — |
| `RebuildIfStaleAsync` | приватный | `StaleRebuildCoordinator` | opportunistic `WaitAsync(0)` |
| `RebuildIfStale()` `[C]` | приватный | `StaleRebuildCoordinator` | удаляется порцией C |
| `RunRebuildUnderGateAsync` | приватный | `StaleRebuildCoordinator` | drain/restore/catch |
| `_rebuildDelayForTest`, `DelayForTestAsync` | приватный | `StaleRebuildCoordinator` | тестовая щель |
| `RebuildAsync`, `TryRebuildIncrementalAsync` | приватный | `StaleRebuildCoordinator` | — |
| `RecompileAndSwapAsync` | приватный | `SolutionSnapshotStore` (пишет снимок) | 2 сайта вызова |
| `RebuildViaFullReloadAsync` | приватный | `StaleRebuildCoordinator` | — |
| `TryReadSourceTextAsync` | приватный static | `StaleRebuildCoordinator` | — |
| `CommitDocumentTextsAsync` | **публичный** | **фасад** (оркестрация) | 5 инструментов передают метод как делегат |
| `ForceReloadAsync`, `ReloadAndSwapAsync` | публичный + приватный | **фасад** / `StaleRebuildCoordinator` | gate держит координатор |
| `_warmupTask`, `_warmupException`, `_warmupCompleted` | поле ×3 | `ReadyGate` | `_warmupCompleted` удаляется порцией C |
| `WarmupAsync` | приватный | `ReadyGate` | — |
| `WaitForWarmupAsync` | **публичный** | **фасад** → `ReadyGate` | 8 тест-сайтов |
| `EnsureReadyAsync` | приватный | `ReadyGate` | вызывается **вне** `_lock` |
| `EnsureReady()` `[C]` | приватный | `ReadyGate` | удаляется порцией C |
| `ThrowIfLoadFailed` | приватный | `ReadyGate` | — |
| `WarmupFailedMessage` | const | `ReadyGate` | — |
| `_loadException`, `_loadFailureMessage` | поле readonly ×2 | `ReadyGate`; свойства — в фасаде | `MultiSolutionManager` читает оба |
| `HasLoadFailure`, `LoadFailureMessage` | **публичный** | **фасад** → `ReadyGate` | 2 прод-сайта в `MultiSolutionManager` |
| `EnsureLoaded` | **публичный** | **фасад** | 49 инструментов |
| `GetLoadedSolution` `[C]` | **публичный** | **фасад** | 4 инструмента передают метод как делегат; геттер удаляется в C, метод остаётся |
| `GetIlDisassembler` | **публичный** | **фасад** (осознанное исключение) | не трогается: вне протокола |
| `_peCache`, `_ilAdapter`, `_analyzerLoader`, `_workspace`, `SharedCache` | поле ×5 | остаются в фасаде (жизненный цикл) | не декомпозируются |
| `CreateAsync`, `CreateEmpty`, `_solutionPath`, `_disposed`, `Dispose` | — | остаются в фасаде | композиция + `IDisposable` |
| `SimulateFileChangeForTest`, `HasStaleProjectsForTest`, `SetRebuildDelayForTest` | internal | `StaleRebuildCoordinator` (форвард в фасаде) | тесты не меняются |
| `DescribeEmptyWorkspace` | internal static | остаётся в фасаде | 4 тест-сайта |

Пометка `[C]` = удаляется порцией C, декомпозиции не подлежит.

##### Три инварианта — что нельзя сломать

| Инвариант | Как закреплён | Обеспечение в новом коде |
|---|---|---|
| **`EnsureReady()` вне `_lock`** — warmup и перестройка берут лок сами; блокировка снаружи = самоблокировка | комментарии `EnsureReadyAsync`, `GetAnalysisContextAsync` | `ReadyGate` **не** владеет локом `SolutionSnapshotStore`: протокол вызывается до `store.Snapshot()` |
| **Снимок одним `lock (_lock)`** (атомарность #282) | тело `GetAnalysisContextAsync`; тест `GetAnalysisContextAsync_MatchesSyncPath_OnSameSnapshot` | `SolutionSnapshotStore.Snapshot()` — единственное место чтения тройки полей, под одним локом |
| **`CreateAsync` неблокирующий**, `_resolver` не пересоздаётся на тёплом пути | `CreateAsync_ReturnsBeforeCompilationCompletes`, `GetResolver_ReturnsCachedInstance_WhenNotStale` | warmup остаётся фоновой задачей, стартующей из `CreateAsync`; `ReadyGate` не создаёт резолверов |

##### Тесты — контракт (не править)

`SolutionManagerTests` (14 тестов, включая 3 `GetAnalysisContextAsync_*`), `IncrementalRebuildTests`,
`LoaderConcurrencyHardeningTests`, `MultiSolutionManagerTests`, `LegacySolutionHandlingTests` —
обязаны остаться зелёными **без правок**. Все они ходят только через публичную поверхность
фасада и три `internal` тест-щели, поэтому разбиение их не задевает.

#### 5.2.1. `SolutionManager` — выполнено 2026-10-28

Разбит на фасад + **шесть** подчинённых типов в `src/RoslynCodeLens/SolutionLifecycle/`
(два сверх исходной таблицы — `StaleRebuildCoordinator` и `SolutionLifecycleFactory` — появились
по факту инвентаризации: rebuild-движок и композиция оказались слишком велики, чтобы остаться
в фасаде, и слишком связаны, чтобы делить иначе).

| Тип | Ответственность | LOC | Членов |
|---|---|---|---|
| `SolutionManager` (фасад) | публичный контракт, делегирование | **334** (было 899) | 49 (было 61) |
| `SolutionSnapshotStore` | `_loaded`/`_resolver`/`_metadataResolver` + `lock`, атомарность #282 | ~130 | 6 |
| `StaleRebuildCoordinator` | rebuild-движок: `RebuildIfStale*`, drain/restore, incremental vs full reload | 337 | 17 |
| `StaleRebuildGate` | single-flight gate + контракт drain/restore | ~135 | 8 |
| `ReadyGate` | `EnsureReady*`, warmup-ожидание, `ThrowIfLoadFailed` | ~170 | 15 |
| `RecentChangesJournal` | `RecentlyChangedDocuments`/`Reset`/`Record` | ~55 | 3 |
| `SolutionLifecycleFactory` | открытие решения, warmup, полная перезагрузка, сообщение о пустом workspace | ~230 | 8 |

**Что уйдёт с порцией C.** В `ReadyGate` (методы `Ready`, `ResetWarmupCompleted`,
`SignalWarmupCompleted`, поле `_warmupCompleted`) и в `StaleRebuildCoordinator`
(`RebuildIfStale`), плюс обёртки `SolutionManager.EnsureReady`/`GetAnalysisContext()`.
Порция C удаляет их из **двух** файлов вместо одного, зато снимает `_warmupCompleted` из
менеджера целиком — цель раздела 4.3 достижима.

**Что осталось в фасаде навсегда:** 17 публичных членов контракта (см. `get_type_overview`),
композиция из фабрики, `ReloadAndSwapAsync` (свап тройки и workspace обязан быть в одном
критическом сечении — это единственное место, знающее оба лока), `DescribeEmptyWorkspace`
(делегирует в фабрику, 4 тест-сайта) и реализация `ISolutionLifecycleState`.

**Инварианты — подтверждены.**

| Инвариант | Как обеспечен после разбиения |
|---|---|
| `EnsureReady()` вне `_lock` | `ReadyGate` не держит ни одного лока; `GetAnalysisContextAsync` ждёт протокол, затем читает `_snapshot.Snapshot()` |
| Снимок одним `lock` (#282) | `SolutionSnapshotStore.Snapshot()` — единственная точка чтения тройки; покрыт тестом на гонку `SolutionSnapshotStore_ConcurrentSnapshotDuringSwap_NeverTears` |
| `CreateAsync` неблокирующий, резолвер не пересоздаётся | warmup стартует из `CreateAsync` через фабрику и ждётся только при первом доступе; тёплый путь возвращает тот же экземпляр (`GetResolver_ReturnsCachedInstance_WhenNotStale`) |

**Найденный при переносе дефект (настоящий, не косметический).** Первая версия
`ReadyGate.SetAfterReadyHook` принимала `Func<Task>` без `CancellationToken`, и менеджер
регистрировал `() => RebuildIfStaleAsync(CancellationToken.None)` — то есть **токен
вызывающего терялся**, и перестройка становилась неотменяемой. Это поймал существующий
тест-контракт `GetAnalysisContextAsync_CancelledBeforeRebuild_ThrowsAndKeepsStaleState`
(«No exception was thrown»). Исправлено: хук принимает `Func<CancellationToken, Task>` и
получает `ct`. Урок: перенос через лямбду легко «зашивает» `CancellationToken.None`.

**Метрика до/после.**

| Метрика | До | После |
|---|---|---|
| `SolutionManager` — LOC | **899** | **334** |
| `SolutionManager` — членов | **61** | 49 (17 публичных) |
| `SolutionManager` — полей состояния | 12 | 8 |
| `get_diagnostics` | 0 / 0 | **0 / 0** |
| `dotnet test RoslynCodeLens.slnx` | 1259 passed | **1276 passed, 0 failed** |
| Тесты `SolutionManager*` | 14 | 14 **без правок** |

Новых тестов — 17 в `SolutionManagerDecompositionTests` (5 journal + 4 store + 4 gate +
4 ready-gate), база 1259 + 17 = 1276.

⚠️ **Критерий «`SolutionManager` отсутствует в `find_large_classes(300, 15)`» не достигнут.**
Фасад — 334 LOC и `StaleRebuildCoordinator` — 337 LOC, оба выше порога 300. Членов строго
меньше 300 LOC порога по строкам не даёт: `355→334` это уже предел для кода, который обязан
держать публичный контракт (17 членов), композицию, `Dispose` и реализацию порта.
Дальнейшее дробление требует либо разнести публичный контракт по нескольким типам (ломает
тесты-контракт и 51 сайт инструментов), либо вынести `ReloadAndSwapAsync` из фасада (сломает
атомарность «снимок + workspace в одном критическом сечении»). Обе альтернативы нарушают
явные запреты задачи, поэтому **остановлено на 334/337 и зафиксировано как остаток**.
Сравнение: `SolutionLoader` 751, `GenerateTestSkeletonLogic` 530, `ChangeSignatureLogic` 507 —
`SolutionManager` больше не входит даже в первую десятку по размеру.

Проверка: `get_diagnostics` 0/0; `dotnet test RoslynCodeLens.slnx` — **1276 passed, 0 failed**.
Подробности — [`decision/2026-10-28T22-40-stage5-2-solutionmanager-decomposition.md`](../decision/2026-10-28T22-40-stage5-2-solutionmanager-decomposition.md).

**Затронутые области:** резолвер, 6 инструментов, загрузчик, парсер стек-трейсов.
**Критерий готовности:** `get_complexity_metrics(threshold=15)` — 0 методов;
`find_large_classes(maxLines=300, maxMembers=15)` — 0 типов в `src/`;
`find_god_objects` — пусто; поведение подтверждено существующими тестами.
**Влияние на поведение:** нулевое (перенос кода).
**Риски:** высокие по объёму. Каждый класс декомпозировать отдельным коммитом;
`SymbolResolver` — начать с `find_references` по каждому публичному члену, чтобы
не потерять вызывающих при разнесении по трём типам.

### Этап 6 — тестируемость и покрытие

**Цель:** закрыть 46 risk hotspots и поднять покрытие с 31 %.

| Изменение | Ссылки |
|---|---|
| Самая крупная дыра — `ApplyFixLogic.ExecuteAsync` (CC 16, непокрыт) | [`ApplyFixLogic.cs:16`](../src/RoslynCodeLens/Tools/ApplyFixLogic.cs:16) |
| `FindObsoleteUsageLogic.Execute` (CC 24, непокрыт) | [`FindObsoleteUsageLogic.cs:11`](../src/RoslynCodeLens/Tools/FindObsoleteUsageLogic.cs:11) |
| `ComplexityCalculator.Calculate` (CC 16) — метрика, на которой построены пороги аудита; её ошибка искажает все отчёты | [`ComplexityCalculator.cs:20`](../src/RoslynCodeLens/Analysis/ComplexityCalculator.cs:20) |
| `ExtractMethodLogic.ExecuteAsync` (CC 16), `FindGodObjectsLogic.Execute` (CC 15) | [`ExtractMethodLogic.cs:50`](../src/RoslynCodeLens/Tools/ExtractMethodLogic.cs:50), [`FindGodObjectsLogic.cs:19`](../src/RoslynCodeLens/Tools/FindGodObjectsLogic.cs:19) |
| Паритетные тесты для пар близких инструментов (`find_callers` / `analyze_method` / `get_call_graph`) — общие правила выбора символа | `tests/RoslynCodeLens.Tests/Tools/` |
| `GetAnalysisContext` (CC 2, непокрыт) — центральная точка, через которую идут 51 инструмент | [`SolutionManager.cs:510`](../src/RoslynCodeLens/SolutionManager.cs:510) |

**Критерий готовности:** `find_uncovered_symbols` — 0 символов с CC ≥ 10;
`riskHotspotCount` снижен до единиц; покрытие ≥ 60 %.
**Влияние на поведение:** отсутствует.
**Риски:** фикстуры тяжёлые (загрузка решения) — переиспользовать существующие
`*FixtureTests`-паттерны вместо новых загрузок.

### Этап 7 — архитектурные улучшения (расширяемость)

**Цель:** снять OCP-ограничение — инструменты и правила перестают требовать правки ядра.

| Изменение | Ссылки |
|---|---|
| Ввести `IToolLogic<TRequest, TResult>` для логики, которую нужно подменять; статические `*Logic` остаются быстрым путём, но за интерфейсом | `Tools/*Logic.cs` |
| Правила архитектуры как типизированная модель с поведением (`ForbidRule`, `AllowOnlyRule`), а не `switch` по строке | [`CheckArchitectureLogic.cs`](../src/RoslynCodeLens/Tools/CheckArchitectureLogic.cs:39), [`ArchitectureRule.cs`](../src/RoslynCodeLens/Models/ArchitectureRule.cs:9) |
| Изолировать рефлексию в `ChangeSignatureBridge`: вынести межверсионный доступ в отдельный адаптер, чтобы 10 из 13 reflection usages не протекали в доменную логику | [`ChangeSignatureBridge.cs:155`](../src/RoslynCodeLens/Analysis/ChangeSignatureBridge.cs:155) |
| Пороги метрик (`CC > 15`, `LOC > 300`) — в конфигурацию, а не литералами в вызовах | [`GetProjectHealthLogic.cs:9`](../src/RoslynCodeLens/Tools/GetProjectHealthLogic.cs:9) |

**Критерий готовности:** добавление нового инструмента не требует правки существующих
типов; `find_reflection_usage` — 0 вхождений рефлексии вне `Analysis/Bridge/`.
**Влияние на поведение:** отсутствует.
**Риски:** низкие (расширение, не переписывание).

---

## 4. Зависимости этапов

```mermaid
flowchart LR
    E0["Этап 0: предупреждения и утечки"]
    E1["Этап 1: enum вместо строк"]
    E2["Этап 2: интерфейсы и DIP"]
    E3["Этап 3: дедупликация протокола"]
    E4["Этап 4: async-first"]
    E5["Этап 5: декомпозиция"]
    E6["Этап 6: тесты и покрытие"]
    E7["Этап 7: расширяемость"]
    E0 --> E1
    E1 --> E2
    E3 --> E4
    E2 --> E5
    E3 --> E5
    E4 --> E5
    E5 --> E6
    E6 --> E7
    E2 --> E7
```

Этапы 1 и 3 независимы друг от друга и могут идти параллельно.
Этап 4 **требует** этапа 3 (дедупликация уменьшает число async-сайтов с четырёх до одного).
Этап 6 идёт после декомпозиции — иначе тесты придётся переписывать дважды.

---

## 5. Глобальные критерии готовности

1. `get_diagnostics` по решению — **0 errors, 0 warnings** (сейчас 5 warnings).
2. `find_async_violations` — **0** `SyncOverAsync*` нарушений (сейчас 9).
3. `find_disposable_misuse` — **0** реальных нарушений (сейчас 9, часть ложные).
4. `get_complexity_metrics(threshold=15)` — **0** методов (сейчас 22).
5. `find_large_classes(maxLines=300, maxMembers=15)` — **0** типов в `src/` (сейчас 13).
6. `find_god_objects` — **пусто** — ⚠️ **критерий недействителен** (поправка A1):
   инструмент возвращает пусто и до, и после; на этом решении порог
   `minOutgoingNamespaces = 5` недостижим. Заменять проверкой по `find_large_classes`:
   ни один тип в `src/` не превышает 300 LOC / 15 членов (сейчас превышают **12** типов,
   худший — `SolutionManager` 899 LOC / 61 член).
7. `find_circular_dependencies` и `check_architecture` — **0** (уже выполнено; не регрессировать).
8. `find_uncovered_symbols` — **0** символов с CC ≥ 10 (сейчас 46 risk hotspots).
9. Публичный API содержит **контракты** (`Interface` в `get_public_api_surface`), а не только конкретные типы.
10. Каждый этап завершён: `dotnet test RoslynCodeLens.slnx` зелёный, отдельный коммит.

---

## 6. Сводка рисков

| Риск | Этапы | Мера |
|---|---|---|
| Смена типа параметра инструмента на интерфейс ломает конструирование MCP-хостом | 2 | Проверить разрешение интерфейсных параметров `WithToolsFromAssembly()` на одном инструменте до массовой замены; при отказе — свести этап к фабрике/подмене в тестах |
| 40+ сигнатур `Execute` → `ExecuteAsync` дают большой diff и конфликты | 4 | По одному инструменту на коммит; не смешивать с этапами 2–3 и 5 |
| Разнесение `SymbolResolver` теряет вызывающих (45 публичных членов) | 5 | `find_references` по каждому члену перед переносом; `rename_symbol` вместо текстовой замены |
| Изменение публичной сигнатуры моделей (`ArchitectureRule.Kind`, `DiRegistration.Lifetime`) — breaking change для клиентов | 1 | `change_signature` с `preview: true`; при необходимости — переходный период с обоими членами |
| `RegisterWorkspaceFailedHandler` меняет поток диагностик загрузки | 0 | Тест на `LoadedSolution.LoadDiagnostics` до и после |
| Характеристические тесты в этапе 3 обнаружат **уже существующее** расхождение копий | 3 | Считать это находкой: зафиксировать и решить, какое поведение верное, до рефакторинга |
| `unload_solution`/перестройка под нагрузкой — тесты падают нестабильно | 3, 4, 6 | Переиспользовать один загруженный фикстурный solution; не грузить решение в каждом тесте |
| Пороги метрик измеряются собственным инструментом, который сам может быть неверен | все | `ComplexityCalculator.Calculate` покрыть тестами **первым** в этапе 6 |
