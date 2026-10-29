# Этап 5.5: следующая тройка методов по сложности

- Дата (UTC): 2026-10-29T00:27Z
- Режим/агент: code (скилл `solid-plan-implementation`)

## Что сделано

Три худших по CC метода после 5.4 разобраны по ответственностям. Поведение не менялось,
тесты-контракт **не правились**, новых тестов не добавлялось (пункт — чистый перенос;
неизменность набора кадров/находок и есть требуемое доказательство). Три коммита, по одному на метод.

| Метод | CC до | CC после | Cognitive до | Cognitive после |
|---|---|---|---|---|
| `StackTraceParser.TryParseFrame` | 26 | **4** | 29 | **5** |
| `GetCallGraphLogic.EnumerateOutgoingCalls` | 25 | **5** | 37 | 6 |
| `CheckArchitectureLogic.ExecuteCore` | 22 | **4** | 55 | **8** |

**`TryParseFrame` — по формату кадра.** `StripFrameSuffixes` (`in File.cs:line N` + Native-AOT
`+ 0xNN`), `EnumerateFrameSuffixes` (Demystifier lambda), `TryPeelLocalFunctionSuffix`
(`+LocalFunc(args)`), `SplitTypeAndMethod` (последняя точка верхнего уровня, `.ctor`/`.cctor`,
срез `[TKey]`). Инвариант порядка зафиксирован в XML-doc: шаги — **не альтернативы**, а строго
упорядоченная цепочка, и сбой любого шага обрывает кадр (не передаёт управление следующему
формату). Особо: lambda-суффикс снимается до поиска последней `(...)`-группы, а снятие local
function **заменяет** список параметров и потому обязано идти до его чтения.

**`EnumerateOutgoingCalls` — по форме вызова.** Метод обходит/дедуплицирует; `ResolveCallee` —
`switch` по синтаксическому виду, каждый case в своём методе: `TryInvocation`, `TryConstructor`,
`TryPropertySet`, `TryPropertyGet`, `TryBinaryOperator` (все CC ≤ 4). Case-ветви взаимоисключающи
по виду узла, поэтому их порядок (в отличие от парсера кадров) смысла не несёт. Семантика внешних
узлов сохранена: у метода без source-location нет тела → обход ничего не даёт, и именно это делает
BCL/NuGet-кали `isExternal = true` терминальными листьями. Лимит `maxNodes` и правило
«edge to truncated target still recorded» остались в `WalkCallees` нетронутыми.

**`ExecuteCore` — по виду правила.** `EnumerateScannedTrees` (два source-side фильтра, модель
создаётся только для выживших деревьев), `CanBeSource`, `ResolveReference` (отбрасывает
self-references), `IsEligibleForAllowOnly`, `RecordViolations`, `MatchForbid`, `TryMatchAllowOnly`,
`BuildSite`. Два тонких правила сохранены и задокументированы в месте, которое на них опирается:
`allowOnly` оценивает только solution-internal и не-сгенерированные цели (`forbid` — всё, включая
метаданные и сгенерированное); self-references не доходят ни до одного вида правила. Мемоизация
(`SolutionScopeNames`, кэш `generatedTargets`) и hoisted `HasAllowOnly` переехали в новый
`ScanContext`, поэтому по-прежнему живут весь скан, а не пересчитываются на каждом узле имени.

## Зачем

Все три метода были не «сложными», а **многоответственными**: парсер смешивал четыре формата кадра
в одном теле, обход графа — распознавание формы вызова с обходом тела, а проверка архитектуры —
сканирование, резолвинг скоупов и две намеренно разные семантики правил. После разнесения
`switch`-ветви стали отдельными методами, а не ветками внутри одного тела.

⚠️ Отличие от 5.4: там было два способа доказать корректность (зелёные тесты + новые тесты
паритета «детектор ↔ агрегат»). Здесь новых тестов нет осознанно: для парсера кадров и проверки
архитектуры контракт уже полностью описан существующими тестами (39 и 47), а добавление
паритетных тестов потребовало бы дублировать приватную декомпозицию в тест — то есть закрепить
структуру там, где нужен только результат.

## Инструменты: почему не `extract_method`

Справедливо то же, что в 5.4:

- `extract_method` вырезает один **связный диапазон** строк. В `TryParseFrame` шаги не совпадают с
  диапазонами: чтобы получить связные куски, тело сначала надо было переписать в именованную
  цепочку шагов — ручное редактирование всё равно предшествовало бы извлечению.
- `rename_symbol` с `renameInComments: true` переписывает XML-doc в **других** файлах по общим
  именам (грабли 5.3); здесь понадобились бы общие имена `Resolve`, `Try*`, `Build*`, что
  рискованнее самой правки.

Итог: ручное написание через `apply_diff`; `rename_symbol` / `change_signature` /
`apply_code_action` **не вызывались** — на диск через MCP ничего не писалось.

## Метрика до/после

| Метрика | До | После |
|---|---|---|
| Методов `src/` с CC ≥ 16 | 16 | **13** |
| `get_complexity_metrics(15)` — максимальный CC нового метода | — | **5** |
| `StackTraceParser.cs` | 306 LOC / `TryParseFrame` CC 26 | `TryParseFrame` CC 4, 4 новых метода |
| `GetCallGraphLogic.cs` | `EnumerateOutgoingCalls` CC 25 | CC 5, 6 новых методов |
| `CheckArchitectureLogic.cs` | `ExecuteCore` CC 22 / cognitive 55, nesting 5 | CC 4 / cognitive 8, 8 новых методов |
| `get_diagnostics` | 0 / 0 | **0 / 0** |
| `dotnet test RoslynCodeLens.slnx` | 1296 passed | **1296 passed, 0 failed** |

## Набор находок не изменился — как это проверено

Тесты-контракт **не правились** и остались зелёными:

- `StackTraceParserTests` + `StackFrameDemanglerTests` — 39/39. Покрывают: runtime-кадр с
  `in File.cs:line N` и без, log-префикс, цепочку внутренних исключений, mangled async/lambda/local
  function, generic arity, demystified `async Task<int>`, `.ctor`, шум/разделители, пустой ввод и
  verbatim-фикстуры современного .NET.
- `GetCallGraphToolTests` — 18/18. Покрывают: callees/callers/both, внешние листья `isExternal`,
  все виды рёбер (method/operator/constructor/property get/property set), лимиты глубины и узлов.
- `CheckArchitectureLogicTests` + `CheckArchitectureFixtureTests` — 47/47. Покрывают обе семантики
  (`allowOnly` не оценивает framework/метаданные; self-references разрешены при обоих видах),
  скоупы namespace/project, лимит сайтов, сортировку и число созданных semantic-моделей.

## Затронутые файлы

- `src/RoslynCodeLens/StackTrace/StackTraceParser.cs` — `TryParseFrame` разбит по форматам кадра.
- `src/RoslynCodeLens/Tools/GetCallGraphLogic.cs` — `EnumerateOutgoingCalls` разбит по форме вызова.
- `src/RoslynCodeLens/Tools/CheckArchitectureLogic.cs` — `ExecuteCore` разбит по виду правила
  (+ `ScanContext` record и `ResolvedReference` record struct).
- `plans/2026-10-28-solid-architecture-audit.md` — раздел 5.5, три галочки в таблице этапа.
- `decision/2026-10-29T00-27-stage5-5-three-more-methods.md` — эта запись.

## Проверка

- Базовая линия перед правками: `get_complexity_metrics` (26/25/22), `get_diagnostics` 0/0,
  `dotnet test` 1296 passed, дерево чистое.
- После каждого метода: `get_diagnostics` (severity=warning, всё решение) — **0/0**,
  точечный прогон тестов-контракт — зелёный без правок.
- `get_complexity_metrics(15)` — ни `TryParseFrame`, ни `EnumerateOutgoingCalls`, ни `ExecuteCore`
  в списке; все новые методы ≤ 5 (максимум — `EnumerateScannedTrees` CC 9).
- `dotnet test RoslynCodeLens.slnx` после всех трёх — **1296 passed, 0 failed** (8 m 57 s).
- Три отдельных коммита: `a10b753` (парсер), `1fa62dc` (граф вызовов), `c6cd36f` (архитектура).

## Риски / дальше

- `TryParseFrame`: порядок шагов — единственный инвариант, который легко нарушить при будущей
  правке. Он записан в XML-doc `TryParseFrame`; при добавлении нового формата кадра его надо
  вставлять в цепочку осознанно, а не «в любое место».
- `EnumerateOutgoingCalls`: новый вид вызова — новый `Try*` и один case в `ResolveCallee`,
  `EnumerateOutgoingCalls` не меняется.
- `CheckArchitectureLogic`: новый вид правила требует правки `RecordViolations` (диспетчеризация),
  а не `ExecuteCore`. Испытательный шов `modelFactory` сохранён — тесты считают число моделей
  и продолжают это делать.
- Остаток этапа 5: `GenerateTestSkeletonLogic` (530 LOC / 18 членов) — декомпозиция типа, отдельный
  заход. Методы с CC ≥ 16 остаются: `ToTypeInfo` (22), `Members.Resolve` (21), `Walker.Visit` (21),
  `ClassifyValueContext` (21), `ResolveSyntax` (19), `ProjectClosure.Compute` (17),
  `DeadCodeFilters.Classify` (17), `EnumerateCandidates` (17/57 cognitive).
