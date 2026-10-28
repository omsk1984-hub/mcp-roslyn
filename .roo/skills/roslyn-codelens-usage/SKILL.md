---
name: roslyn-codelens-usage
description: Инструкция по работе с MCP-сервером roslyn-codelens (67 инструментов Roslyn-анализа и рефакторинга). Каталог инструментов, жизненный цикл решения, типовые сценарии, известные дефекты и обходные пути, правила безопасности. Использовать перед любым семантическим анализом кода решения.
---

# Скилл: работа с MCP-сервером `roslyn-codelens`

Сервер предоставляет 67 инструментов семантического анализа и рефакторинга
решения C# поверх Roslyn. Запускается из
[`D:\work\MCP\mcp-roslyn\publish\RoslynCodeLens.dll`](.roo/mcp.json:6)
с путём к решению в аргументах; транспорт — stdio.

Планируемое расширение (71 инструмент: `validate_edit`, `format_document`,
`apply_fix`, `extract_method`, плюс скоуп `get_diagnostics` по файлу) —
[`plan/27_roslyn-codelens-server-feature-plan.md`](plan/27_roslyn-codelens-server-feature-plan.md).
До его реализации пользоваться эквивалентами из §3.7.

Единственное решение проекта: [`RoslynCodeLens.slnx`](RoslynCodeLens.slnx)
(3 проекта).

---

## 1. Жизненный цикл решения

Порядок действий при старте сессии:

1. `list_solutions` — какие решения загружены, какое активно (`isActive`).
2. Если решение не загружено — `load_solution` с абсолютным путём к `.slnx`.
   Загрузка ~3 с; для больших решений — `background: true` + опрос
   `get_task_status` по возвращённому `taskId`.
3. `set_active_solution` — переключение при нескольких решениях.
4. После массовых правок файлов / смены `Directory.Build.props` / NuGet —
   `rebuild_solution` (или `start_background_task` с `toolName: "rebuild_solution"`).
5. `unload_solution` — освобождение памяти по завершении работы.

Служебные: `list_running_tasks` (фоновые задачи), `list_trusted_paths`
(состояние доверия), `revoke_trust` (снятие доверия).

### Доверие и анализаторы

- `get_diagnostics` по умолчанию возвращает **только ошибки и предупреждения
  компилятора**. Для диагностик анализаторов (`includeAnalyzers: true`)
  решение должно быть доверенным: `trust_solution` со `scope: "session"`
  (или `"persistent"` / `"addRoot"`).
- Политика анализаторов: `nuget-and-solution-bin` (видна в `list_trusted_paths`).
- `revoke_trust` снимает доверие. **Всегда восстанавливать исходное состояние
  доверия после тестов** (проверить `list_trusted_paths` до и после).

---

## 2. Каталог инструментов (67)

### 2.1. Поиск и навигация

| Инструмент | Назначение | Ключевые параметры |
|---|---|---|
| `search_symbols` | Поиск типов/методов/полей по подстроке | `query`, `limit` |
| `go_to_definition` | Файл и строка определения символа | `symbol` |
| `get_symbol_context` | Пространство имён, база, интерфейсы, DI-зависимости, публичные члены | `symbol` |
| `get_type_overview` | Контекст + иерархия + диагностики файла одним вызовом | `typeName` |
| `get_type_hierarchy` | Базовые типы, интерфейсы, наследники | `symbol` |
| `get_file_overview` | Типы в файле + диагностики (поддерживает .razor/.cshtml) | `filePath` |
| `get_method_source` | Исходник члена(ов) по имени; батчевый | `symbols[]` |
| `get_overloads` | Все перегрузки метода/конструктора с сигнатурами | `symbol` |
| `get_operators` | Операторы и конверсии типа (включая metadata) | `symbol` |
| `get_extension_methods` | Все применимые extension-методы для типа-приёмника | `type`, `nameFilter` |

### 2.2. Ссылки, вызовы, графы

| Инструмент | Назначение | Примечание |
|---|---|---|
| `find_references` | Все ссылки с видом (read/write/invocation/cast/…) | фильтр `kinds` |
| `find_callers` | Все места вызова метода | `symbol` = `Type.Method` |
| `analyze_method` | Сигнатура + вызывающие + исходящие вызовы одним вызовом | |
| `get_call_graph` | Транзитивный граф вызовов (callees/callers/both) | `maxDepth`, `maxNodes`, детекция циклов |
| `analyze_change_impact` | Blast radius: файлы, проекты, вызовы перед изменением символа | |
| `find_implementations` | Все классы/структуры, реализующие интерфейс или наследующие класс | `symbol` = имя типа |

### 2.3. Анализ потока

| Инструмент | Назначение | Ограничение |
|---|---|---|
| `analyze_data_flow` | Объявленные/читаемые/записываемые переменные, захват лямбдами | диапазон строк — **в пределах одного блока операторов** |
| `analyze_control_flow` | Достижимость начала/конца, точки выхода | то же ограничение |
| `get_exception_flow` | Какие исключения покидают метод (с путём и точками перехвата) | **не поддерживает конструкторы** (см. §4, дефект 1) |

### 2.4. Исключения

| Инструмент | Назначение |
|---|---|
| `find_throw_sites` | Все `throw` данного типа исключения (включая производные: `includeDerived`) |
| `find_catch_blocks` | Обработчики: `hasFilter` / `rethrows` / `isEmpty`; `includeBaseClauses` для `catch (Exception)` |

### 2.5. Качество кода

| Инструмент | Назначение |
|---|---|
| `get_complexity_metrics` | Цикломатическая + когнитивная сложность, вложенность; `metric`, `threshold`, `project` |
| `find_large_classes` | Классы по порогам строк/членов |
| `find_god_objects` | Размер × зацепление (входящие/исходящие пространства имён); все пороги настраиваются |
| `find_naming_violations` | PascalCase/camelCase/префиксы I и _ |
| `find_unused_symbols` | Мёртвый код (публичные символы без ссылок; фильтры тестов/генерации/MEF) |
| `find_async_violations` | sync-over-async, async void, забытый await, fire-and-forget |
| `find_disposable_misuse` | Утечки IDisposable: не-освобождённые локальные + выброшенные результаты |
| `find_reflection_usage` | Type.GetType / Activator / MethodInfo.Invoke |
| `find_obsolete_usage` | Использование [Obsolete], группировка по сообщению/серьёзности |
| `find_attribute_usages` | Типы/члены с заданным атрибутом |
| `find_event_subscribers` | Все += / -= по событию (аудит утечек подписок) |
| `get_project_health` | Агрегат 7 измерений по проектам с топ-хотспотами |

### 2.6. Архитектура и зависимости

| Инструмент | Назначение | Примечание |
|---|---|---|
| `check_architecture` | Проверка правил слоёв `forbid` / `allowOnly` по графу типов | `to` — **всегда массив строк**; `allowOnly` игнорирует framework/metadata-цели |
| `find_circular_dependencies` | Циклы в графе проектов или пространств имён | `level: project/namespace` |
| `get_project_dependencies` | Прямые и транзитивные ProjectReference | |
| `get_nuget_dependencies` | Пакеты NuGet по проектам | |
| `get_public_api_surface` | Полный публичный API (942 записи в этом решении) | основа для `find_breaking_changes` |
| `find_breaking_changes` | Дифф текущего API против baseline (.json или .dll) | baseline получают из `get_public_api_surface` |

### 2.7. Тесты

| Инструмент | Назначение | Примечание |
|---|---|---|
| `get_test_summary` | Инвентаризация тестов: фреймворк, атрибуты, строки данных, ссылочные символы | **источник истины** по наличию тестов |
| `find_tests_for_symbol` | Тесты, исполняющие символ (прямые + транзитивные) | **частично ненадёжен** (см. §4, дефект 2) |
| `find_uncovered_symbols` | Публичные методы/свойства без тестов, сортировка по сложности | `riskHotspotCount` в сводке |
| `generate_test_skeleton` | Каркас тестового класса (xUnit/NUnit/MSTest автоопределение) | путь `suggestedFilePath` условный |

### 2.8. DI и генераторы

| Инструмент | Назначение |
|---|---|
| `get_di_registrations` | Регистрации типа в `IServiceCollection`-расширениях |
| `get_instantiation_options` | Как сконструировать тип: конструкторы, фабрики, DI; `requiredMembers` |
| `get_source_generators` | Генераторы и их вывод по проектам |
| `get_generated_code` | Содержимое сгенерированных файлов |

### 2.9. Диагностика и рефакторинг

| Инструмент | Назначение | Режим |
|---|---|---|
| `get_diagnostics` | Ошибки/предупреждения компилятора (+анализаторы при доверии) | чтение |
| `get_code_actions` | Доступные рефакторинги в позиции | чтение |
| `get_code_fixes` | Фиксы конкретного диагностического ID | чтение |
| `apply_code_action` | Применение рефакторинга по заголовку | **по умолчанию preview** |
| `rename_symbol` | Переименование с каскадом (ссылки, конструкторы, nameof, XML-doc) | **по умолчанию preview** |
| `change_signature` | Добавление/удаление/перестановка параметров с обновлением всех вызовов | **по умолчанию preview** |

Все изменяющие инструменты: сначала `preview: true` (по умолчанию), проверка
`edits` и `conflicts`, затем `preview: false` только на чистом дереве git.

### 2.10. Внешние сборки и стектрейсы

| Инструмент | Назначение |
|---|---|
| `inspect_external_assembly` | summary / namespace-режим по закрытой сборке |
| `peek_il` | IL одного метода (полное имя с типами параметров) |
| `resolve_stack_trace` | Маппинг стектрейса на файл/строку/символ |

---

## 3. Типовые сценарии

### 3.1. «Что затронет изменение метода X?»

1. `analyze_change_impact` (symbol = X) — файлы, проекты, прямые ссылки.
2. `get_call_graph` (direction = callers, maxDepth = 3) — транзитивные вызывающие.
3. `find_tests_for_symbol` + `get_test_summary` — какие тесты прикроют изменение.

### 3.2. «Безопасно ли вызывать метод X?»

1. `get_exception_flow` (method = X) — какие исключения покидают метод, где
   перехватываются. Для конструкторов — только `find_throw_sites`.
2. `find_catch_blocks` по типу исключения — кто глотает (`swallowing` в сводке).

### 3.3. «Где слабые места проекта?»

1. `get_project_health` (hotspotsPerDimension = 5) — единый срез.
2. Детализация: `get_complexity_metrics`, `find_god_objects`,
   `find_async_violations`, `find_disposable_misuse`, `find_naming_violations`.

### 3.4. «Нарушена ли слоистость?»

1. `find_circular_dependencies` (level = project, затем namespace).
2. `check_architecture` с правилами вида
   `{"kind": "forbid", "from": "A.*", "to": ["B.*"]}` — поле `to` всегда массив.
3. `get_project_dependencies` — проверка фактического графа ссылок.

### 3.5. «Какие публичные члены никто не тестирует?»

1. `find_uncovered_symbols` — список, отсортированный по сложности.
2. `generate_test_skeleton` для хотспотов — каркас теста.

### 3.6. Проверка правки (post-edit validation)

Полный чек-лист — скилл `edit-validation-protocol`. Минимум:

1. Правка `.cs` внутри решения → `get_diagnostics` по затронутому проекту,
   `severity: error`; 0 ошибок → повторить с `severity: warning`.
2. Изменилось имя/сигнатура → править не текстом, а `rename_symbol` /
   `change_signature` (`preview: true` → `conflicts` → `preview: false`).
3. Символ удалён/перенесён → `find_references` == 0.
4. Правка затронула контракт → `find_implementations` + `get_type_hierarchy`.
5. `.csproj` / props / NuGet / новый файл → `rebuild_solution`; правки
   **только `.cs`** reload не требуют (сервер делает инкрементальный
   rebuild через `FileChangeTracker` + `RebuildIfStale`, а после правок
   самого MCP — через `CommitAsync`).

### 3.7. Разбор падения из лога

1. Вставить полный текст `Exception.ToString()` в `resolve_stack_trace`.
   Формат кадров с файлом: `... in D:\path\File.cs:line 30` (именно `:line N`).
2. Для каждого кадра: `get_method_source` / `analyze_data_flow` по диапазону.

---

## 4. Известные дефекты и обходные пути (проверено 2026-10-25)

### Дефект 1: `get_exception_flow` не поддерживает конструкторы

- Симптом: `SymbolNotFound: Source method 'Type.Type' not found` даже при
  полном квалифицированном имени.
- Обход: для исключений из конструкторов использовать `find_throw_sites`
  (он видит конструкторы) и `find_catch_blocks`.

### Дефект 2: `find_tests_for_symbol` находит не все прямые тесты

- Симптом: пустой или неполный `directTests` для символов, которые тесты
  вызывают через локальную переменную (`registry.InvokeAsync(...)`) или через
  `Assert.Throws`-делегат; при этом `get_test_summary` эти тесты видит.
- Обход: **источник истины по покрытию — `get_test_summary`** (поле
  `referencedSymbols` каждого теста) и `find_uncovered_symbols`.
  `find_tests_for_symbol` — только как быстрый эвристический поиск.

### Дефект 3 (нюанс): диапазон `analyze_data_flow` / `analyze_control_flow`

- Симптом: `Internal: statements not within the same statement list`, если
  диапазон строк пересекает границы блоков (например охватывает весь метод с
  `try`/`catch`).
- Обход: выбирать диапазон внутри **одного** списка операторов (одной пары
  `{ }`), не включая строки с открывающей/закрывающей скобкой соседних блоков.

### Нюанс 4: формат кадров `resolve_stack_trace`

- Кадры вида `... File.cs:30` остаются `unresolved`; корректно резолвятся
  кадры формата `Exception.ToString()` с `:line 30`.

### Нюанс 5: `check_architecture`

- Поле `to` — строго массив строк (одиночная строка → ошибка десериализации).
- `allowOnly` не учитывает ссылки на framework и metadata — для их запрета
  писать явный `forbid`.

### Нюанс 6: `generate_test_skeleton`

- `suggestedFilePath` использует префикс `tests/` и может указать не тот
  тестовый проект. Путь и пространство имён каркаса сверять с фактической
  структурой (`tests/RoslynCodeLens.Tests/<Область>/`).

### Нюанс 7: `get_extension_methods`

- В выдачу попадает сгенерированный код из `obj/` (например RegexGenerator)
  с `origin: source` — учитывать при интерпретации.

### Дефект 8: файлы вне загруженного решения невидимы всем инструментам

- Симптом: `FileNotFound` у `get_file_overview` / `get_code_actions` /
  `apply_code_action`, пустой `find_references`; при этом файл существует.
- Причина: документ ищется только по `Solution.Projects[].Documents[].FilePath`
  загруженного `.slnx`.
- Обход: `list_solutions` → при необходимости `load_solution` другого решения
  (см. `switch-solution`). Если файл вообще не C# и не входит в какое-либо
  решение — семантический анализ невозможен, остаются `read_file` /
  `search_files`.

### Нюанс 9: `apply_diff` атомарен — «ни один блок не применился»

- Симптом: правка не применилась целиком, потому что один SEARCH-блок не
  совпал.
- Причина: `apply_diff` — атомарная операция; это не дефект Roslyn.
- Обход: SEARCH-строки брать из `get_method_source` / `get_file_overview`
  (фактический текст с исходным форматированием), а не по памяти; для
  структурных преобразований — `get_code_actions` → `apply_code_action`
  (правку делает Roslyn, «не совпасть» она не может).

---

## 5. Правила безопасности

1. **Изменяющие инструменты** (`rename_symbol`, `change_signature`,
   `apply_code_action`, `get_code_fixes` с применением) — только через
   `preview: true`, только на чистом дереве git, только после проверки
   `conflicts` в ответе.
2. **Не вызывать** `rebuild_solution` синхронно посреди длинного анализа без
   необходимости — он переоткрывает и перекомпилирует всё решение (~7 с на
   14 проектах, но растёт с размером).
3. **Доверие**: `trust_solution` со `scope: "persistent"` меняет `trust.json`
   на диске — спрашивать пользователя. Для временных проверок — `"session"`.
4. **Не выгружать** активное решение, пока другая фоновая задача
   (`list_running_tasks`) не завершена.
5. Результаты `find_unused_symbols` — кандидаты, а не приговор: проверять
   `find_references` вручную перед удалением (рефлексия, сериализация,
   DI-активация не видны как ссылки).

## 6. Производительность

- Все инструменты с пагинацией: задавать `limit` осознанно (по умолчанию 100–500).
- Тяжёлые запросы (`get_call_graph` с `direction: both`, `get_public_api_surface`)
  возвращают большие ответы — сужать проект/символ.
- `get_project_health` с `hotspotsPerDimension: 0` — быстрый режим «только счётчики».
