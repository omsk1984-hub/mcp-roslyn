# MCP-сервер Roslyn CodeLens

[![MCP Toplist](https://mcptoplist.com/badge/io.github.MarcelRoozekrans%2Froslyn-codelens.svg)](https://mcptoplist.com/server/io.github.MarcelRoozekrans%2Froslyn-codelens)

[![NuGet](https://img.shields.io/nuget/v/RoslynCodeLens.Mcp?style=flat-square&logo=nuget&color=blue)](https://www.nuget.org/packages/RoslynCodeLens.Mcp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/RoslynCodeLens.Mcp?style=flat-square&color=green)](https://www.nuget.org/packages/RoslynCodeLens.Mcp)
[![npm](https://img.shields.io/npm/v/roslyn-codelens-mcp?style=flat-square&logo=npm&color=cb3837)](https://www.npmjs.com/package/roslyn-codelens-mcp)
[![Build Status](https://img.shields.io/github/actions/workflow/status/MarcelRoozekrans/roslyn-codelens-mcp/ci.yml?branch=main&style=flat-square&logo=github)](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/actions)
[![codecov](https://codecov.io/gh/MarcelRoozekrans/roslyn-codelens-mcp/graph/badge.svg)](https://codecov.io/gh/MarcelRoozekrans/roslyn-codelens-mcp)
[![License](https://img.shields.io/github/license/MarcelRoozekrans/roslyn-codelens-mcp?style=flat-square)](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/blob/main/LICENSE)
[![Docs](https://img.shields.io/badge/docs-GitHub%20Pages-blue?style=flat-square)](https://marcelroozekrans.github.io/roslyn-codelens-mcp/)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?style=flat&logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

MCP-сервер на базе Roslyn, который даёт ИИ-агентам глубокое семантическое понимание .NET-кодовых баз — иерархии типов, графы вызовов, регистрации DI, диагностики, рефакторинг и многое другое.

<a href="https://glama.ai/mcp/servers/MarcelRoozekrans/roslyn-codelens-mcp">
  <img width="380" height="200" src="https://glama.ai/mcp/servers/MarcelRoozekrans/roslyn-codelens-mcp/badge" alt="roslyn-codelens-mcp MCP server" />
</a>

<!-- mcp-name: io.github.MarcelRoozekrans/roslyn-codelens -->

---

## Хостинг-развёртывание

Доступно хостинг-развёртывание на [Fronteir AI](https://fronteir.ai/mcp/marcelroozekrans-roslyn-codelens-mcp).

## Возможности

- **find_implementations** — Найти все классы/структуры, реализующие интерфейс или наследующиеся от класса
- **find_callers** — Найти все места вызова метода, свойства или конструктора
- **find_event_subscribers** — Все места += / -= для символа события, с разрешённым обработчиком и меткой подписки/отписки
- **find_tests_for_symbol** — Список методов xUnit/NUnit/MSTest, проверяющих производный символ; опциональный транзитивный обход через вспомогательные методы
- **get_test_summary** — Инвентаризация тестовых методов по проектам: фреймворк, тип атрибута, число строк данных, расположение и производные символы, на которые они ссылаются
- **find_uncovered_symbols** — Публичные методы и свойства, до которых транзитивно не дотягивается ни один тест; отсортированы по цикломатическому числу для приоритизации
- **generate_test_skeleton** — Генерация компилируемого каркаса тестового класса (в виде текста) для метода или типа. Автоопределение xUnit/NUnit/MSTest; зависимости конструктора выводятся как TodoNotes; возвращается рекомендуемый путь к файлу. Завершает цикл вместе с `find_uncovered_symbols`
- **get_type_hierarchy** — Обход базовых классов, интерфейсов и производных типов
- **get_di_registrations** — Сканирование регистраций DI-сервисов. Распознаёт generic-форму (`AddSingleton<IFoo, Foo>()`), single-generic, пары `typeof` и factory-lambda
- **get_instantiation_options** — «Как сконструировать этот тип?» за один вызов: конструкторы с полным описанием параметров, статические factory-методы, объявленные где угодно в решении (в том числе в отдельном factory-типе), регистрации DI и `required`-члены. Передайте `fromProject`, чтобы узнать, может ли *именно этот* проект реально обратиться к каждому варианту — учитывается `InternalsVisibleTo`, поэтому отвечает на вопрос «может ли мой тестовый проект вызвать этот internal-конструктор?»
- **get_project_dependencies** — Получить граф ссылок между проектами
- **get_symbol_context** — Разовый дамп контекста для любого типа
- **get_public_api_surface** — Перечисление всех public/protected типов и членов в production-проектах; плоский детерминированно отсортированный список, подходящий для ревью API или базовых снимков breaking changes.
- **find_breaking_changes** — Сравнение текущего API с базовым JSON или DLL; отчёт об удалённых членах, изменениях вида и изменениях доступности с уровнем Breaking/NonBreaking.
- **find_reflection_usage** — Обнаружение динамического/рефлексивного использования
- **get_exception_flow** — Какие исключения могут покинуть метод: обход вызываемых методов с ограничением глубины, распространение каждого throw через все окружающие try/catch и отчёт о том, что всё ещё выходит наружу, с путём распространения; metadata-вызываемые методы добавляют задокументированные исключения
- **find_throw_sites** — Все места, где бросается тип исключения, опционально включая производные типы; rethrow помечается
- **find_catch_blocks** — Все catch для типа, опционально через базовые клаузы; помечаются фильтрованные, rethrow-и пустые (проглатывающие) обработчики
- **find_references** — Найти все ссылки на любой символ (типы, методы, свойства, поля, события), каждая с меткой вида (`read`, `write`, `readwrite`, `invocation`, `method_group`, `object_creation`, `cast`, `type_check`, `typeof`, `base_type`, `type_constraint`, `type_argument`, `declaration`, `attribute`, `nameof`, `xml_doc`) и отчёт по каждому вхождению с колонкой; серверная фильтрация через `kinds` (например, `["write","readwrite"]` для мест мутации)
- **go_to_definition** — Найти исходный файл и строку, где определён символ
- **get_method_source** — Полный исходный код объявления (XML-доки, атрибуты, сигнатура, тело — исходное форматирование) для одного или многих членов по имени за один вызов: методы (все перегрузки), конструкторы, свойства, индексаторы, поля, события; статусы по каждому элементу (`ok`/`notFound`/`ambiguous`/`metadata`/`unsupportedKind`), чтобы батч никогда не падал целиком
- **resolve_stack_trace** — Преобразование вставленного .NET-стек-трейса в файл/строку/символ, отменяя мэнглинг имён компилятором (async/iterator state machines, лямбды, локальные функции, generic-арность); обрабатывает цепочки внутренних исключений, строки с лог-префиксом и Demystifier-трейсы
- **get_diagnostics** — Список ошибок, предупреждений компилятора и диагностик Roslyn-анализаторов; ограничение ответа одним файлом (`filePath`), файлами, изменёнными с последнего снапшота (`changedOnly`), или фильтрация по точной `column`
- **get_code_fixes** — Получить доступные code fixes со структурированными текстовыми правками для любой диагностики
- **validate_edit** — Проверка после правки за один вызов: диагностики компилятора для затронутых файлов плюс висячие ссылки на символы, которые вы переименовали, удалили или изменили сигнатуру
- **format_document** — Форматирование C#-файла (или диапазона строк) с `.editorconfig` самого проекта, preview-first; no-op для уже отформатированных файлов ничего не пишет
- **search_symbols** — Фаззи-поиск символов workspace по имени
- **get_nuget_dependencies** — Список ссылок на NuGet-пакеты по проектам
- **find_attribute_usages** — Найти типы и члены, декорированные конкретным атрибутом
- **find_obsolete_usage** — Все места вызова `[Obsolete]`, сгруппированные по сообщению о депрекации и уровню, ошибки первыми; для планирования миграций
- **find_circular_dependencies** — Обнаружение циклов в графах зависимостей проектов или неймспейсов
- **check_architecture** — Проверка правил слоистости, которые вы задаёте (`forbid` и `allowOnly`), по реальному семантическому графу типов, а не по `using`-директивам; нарушения группируются по границе со счётчиком ссылок и примерами мест
- **get_complexity_metrics** — Цикломатическая сложность, когнитивная сложность и максимальная глубина вложенности по членам (методы, конструкторы, свойства, индексаторы, операторы). Цикломатическая считает пути и начинается с 1; когнитивная измеряет, насколько сложно читать код, и начинается с 0. `metric` выбирает, по какой из них применяются порог и сортировка — когнитивная лучше как сигнал приоритета рефакторинга, цикломатическая — как бюджет тестирования
- **find_naming_violations** — Проверка соответствия .NET-конвенциям именования
- **find_async_violations** — Sync-over-async, неправильное использование `async void`, отсутствующие await, fire-and-forget задачи; отчёт по каждому нарушению с уровнем
- **find_disposable_misuse** — Экземпляры `IDisposable`/`IAsyncDisposable`, не обёрнутые в `using`/`await using`/не возвращаемые/не присвоенные полю; уровень error/warning по каждому нарушению.
- **find_large_classes** — Поиск типов, превышающих размер по числу членов или строк
- **find_god_objects** — Типы, сочетающие большой размер с высокой межнеймспейсной связанностью; более точный сигнал, чем просто размер, для нарушений SRP
- **find_unused_symbols** — Обнаружение мёртвого кода через анализ ссылок. Автофильтрация тестовых методов (xUnit/NUnit/MSTest), точек входа MCP-инструментов, вывода source-генераторов, MEF-компонованных сервисов и interop-laid-out полей; счётчики фильтров выводятся в `summary.filteredOut`
- **get_project_health** — Композитный аудит, агрегирующий 7 измерений качества по проектам (сложность, большие классы, именование, неиспользуемые символы, рефлексия, async-нарушения, misuse disposable) со счётчиками и top-N хотспотами inline
- **get_source_generators** — Список source-генераторов и их вывода по проектам
- **get_generated_code** — Просмотр сгенерированного исходного кода от source-генераторов
- **inspect_external_assembly** — Просмотр типов, членов и XML-доков из closed-source NuGet-пакетов и ссылаемых сборок
- **peek_il** — Дизассемблирование любого метода в IL-байткод в стиле ilasm из closed-source или сгенерированных сборок
- **get_code_actions** — Обнаружение доступных рефакторингов и фиксов в любой позиции (извлечение метода, переименование, инлайнинг переменной и другое)
- **apply_code_action** — Выполнение любого Roslyn-рефакторинга по заголовку, с preview-режимом (возвращает diff перед записью на диск)
- **apply_fix** — Применение code fix для конкретной диагностики (id + файл + строка) за один вызов, preview-first, отказ от записи, если фикс введёт новые ошибки компилятора
- **extract_method** — Извлечение диапазона строк в новый метод с именем, которое вы выбираете: фасад над собственным провайдером Roslyn, поэтому параметры и тип возврата выводятся из захваченных локальных; независимое от локали сопоставление действий, preview-first
- **rename_symbol** — Безопасное переименование типа или члена по всему решению через Renamer Roslyn, с preview-режимом, отчётом о конфликтах и проверкой актуальности по правкам на диске
- **change_signature** — Добавление, удаление и перестановка параметров метода с переписыванием всех мест вызова; обрабатывает именованные/опциональные аргументы, `params` и extension-методы, и отчитывается об overrides и реализациях интерфейсов, на которые каскад распространился
- **list_solutions** — Список всех загруженных решений и какое из них сейчас активно
- **set_active_solution** — Переключение активного решения по частичному имени (все последующие инструменты работают с ним)
- **load_solution** — Загрузка дополнительного .sln/.slnx в рантайме и его активация
- **unload_solution** — Выгрузка загруженного решения для освобождения памяти
- **rebuild_solution** — Принудительная полная перезагрузка анализируемого решения
- **start_background_task** — Постановка долгого инструмента (сейчас `rebuild_solution`) в очередь для фонового выполнения; возвращает `taskId` для опроса
- **get_task_status** — Получить текущий статус, результат или ошибку фоновой задачи по её `taskId`
- **list_running_tasks** — Список фоновых задач, выполняющихся или завершившихся за последние 5 минут
- **trust_solution** — Авторизация решения для запуска Roslyn-анализаторов (обязательно перед `get_diagnostics` с `includeAnalyzers: true`)
- **list_trusted_paths** — Просмотр постоянного хранилища доверия + session-доверенных решений
- **revoke_trust** — Отзыв ранее выданного доверия для пути решения
- **analyze_data_flow** — Анализ чтения/записи/захвата переменных в диапазоне операторов (объявленные, читаемые, записываемые, всегда присвоенные, захваченные, входящие/выходящие потоки)
- **analyze_control_flow** — Анализ достижимости ветвлений/циклов в диапазоне операторов (достижимость начала/конца, операторы return, точки выхода)
- **analyze_change_impact** — Показать все файлы, проекты и места вызова, затронутые изменением символа — объединяет find_references и find_callers
- **get_type_overview** — Композитный инструмент: контекст типа + иерархия + диагностики файла за один вызов
- **analyze_method** — Композитный инструмент: сигнатура метода + вызывающие + исходящие вызовы за один вызов
- **get_overloads** — Все перегрузки метода/конструктора (source + metadata) с полным описанием параметров и модификаторов за один вызов
- **get_extension_methods** — Все extension-члены, применимые к типу — включая массивы, nullable и кортежи — из решения и ссылаемых сборок (поэтому LINQ тоже отображается), с использованием собственных правил применимости Roslyn; покрывает C# 14 `extension`-блоки, включая свойства и статические члены
- **get_operators** — Все пользовательские операторы и операторы преобразования для типа (source + metadata) с видом, сигнатурой, параметрами и местом в исходнике. Включает синтезированные record-равенство и .NET 7+ checked-варианты
- **get_call_graph** — Транзитивный граф вызывающих/вызываемых для метода, с ограничением глубины и обнаружением циклов
- **get_file_overview** — Композитный инструмент: типы, определённые в файле + file-scoped диагностики за один вызов

## Форма ответа

Все инструменты, возвращающие списки, оборачивают результаты в единый конверт:

```json
{
  "items": [ ... ],
  "totalCount": 142,
  "truncated": false,
  "limit": 500,
  "summary": { ... }
}
```

Когда `truncated` равно `true`, элементы — это **топ N по естественному порядку сортировки инструмента** (сначала по уровню, сначала худшие, по проектам и т.д.) — обычно это именно то, что нужно. Увеличивайте `limit` только если отсутствующие хвостовые элементы важны для задачи.

Инструменты, включающие агрегат `summary` на сегодня:

- `get_diagnostics` — счётчики `{ error, warning, info, hidden }`
- `find_references` — `{ byProject: { name: count }, byKind: { kind: count } }`
- `find_callers`, `find_attribute_usages` — `{ byProject: { name: count } }`
- `search_symbols`, `find_reflection_usage` — `{ byKind: {...} }`
- `find_unused_symbols` — `{ byKind, filteredOut: { testMethod, testContainer, mcpTool, generated, composition, interop } }`
- `find_naming_violations` — `{ byRule: {...} }`
- `get_complexity_metrics` — `{ max, avg, overThreshold, maxCognitive }` (первые три описывают выбранный `metric`)

Инструменты, возвращающие одиночный объект (`get_type_overview`, `get_symbol_context`, `apply_code_action` и т.д.), возвращают свою собственную форму напрямую — конверт оборачивает только инструменты, возвращающие списки.

## Ответы об ошибках

Когда инструмент не может продолжить (символ не разрешён, решение не доверено, файл не найден, неоднозначное совпадение и т.д.), ответ — контент-блок `isError: true` со структурированным JSON-телом:

```json
{
  "code": "SolutionNotTrusted",
  "message": "Solution 'Foo.sln' is not trusted for analyzer execution. ...",
  "details": { "solutionPath": "C:\\Foo.sln" }
}
```

Коды ошибок (переключайтесь по `code`, чтобы обработать каждый):

- `SymbolNotFound` — тип / метод / свойство не удалось разрешить.
- `SolutionNotTrusted` — `get_diagnostics`, `get_code_fixes` или `apply_fix` запросили анализаторы или провайдеры code-fix, но решение не было авторизовано через `trust_solution`.
- `AmbiguousMatch` — `set_active_solution` / `unload_solution` совпало с несколькими решениями; `details.matches` их перечисляет.
- `FileNotFound` — путь к файлу или базовый снимок не существует (или не входит ни в один загруженный проект).
- `ProjectNotFound` — имя решения не совпало ни с одним загруженным решением.
- `InvalidArgument` — входные данные вызывающего были некорректными, не поддерживаемыми или вне диапазона.
- `Internal` — непредвиденная ошибка, не смоделированная выше; `message` несёт текст базового исключения.

**Отмена:** нативная отмена MCP-фреймворка поддерживается. Отмена запроса `tools/call` в процессе выполнения завершает операцию; долгие инструменты (`get_diagnostics` с анализаторами, `get_code_actions`, `apply_code_action`, `get_code_fixes`) проверяют токен на границах горячих циклов.

## Внешние сборки

Символы metadata-происхождения (из NuGet-пакетов и ссылаемых сборок) — полноправные граждане:

- **Уровень 1 — Навигация** (`find_references`, `find_callers`, `find_implementations`): Принимает имена closed-source типов и членов. Разрешает их из метаданных сборок и отчитывается обо всех местах использования на уровне исходников.
- **Уровень 2 — Инспекция** (`inspect_external_assembly`): Просмотр неймспейсов, типов, членов и XML-доков из любой ссылаемой сборки без дизассемблирования.
- **Уровень 3 — IL** (`peek_il`): Дизассемблирование конкретного метода в аннотированный IL в стиле ilasm с помощью ICSharpCode.Decompiler — полезно для понимания внутренностей NuGet-библиотек.

Результаты, возвращающие местоположение, включают поле `Origin` (`source` или `metadata`) и флаг `IsGenerated`, чтобы отличать рукописный код от closed-source или сгенерированного вывода.

## Конфигурация рантайма

- `ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS` — таймаут загрузки MSBuild на проект (по умолчанию `300`). Когда проект превышает это время при открытии workspace, он записывается как запись `SkippedProjects` с `kind: "Timeout"`, а остальная часть решения всё равно загружается. Полезно, когда legacy или некорректный проект подвешивает подпроцесс `BuildHost-net472`.

## Безопасность: модель доверия

`get_diagnostics`, `get_code_fixes` и `apply_fix` могут загружать Roslyn-анализаторы и провайдеры code-fix — DLL, выполняющиеся в процессе. Чтобы предотвратить автоматический запуск недоверенных анализаторов, этот сервер использует модель доверия в стиле VS/Rider:

- **Решения, переданные через CLI при старте**, автоматически доверяются для текущей сессии.
- **Другие решения** должны быть явно доверены через MCP-инструмент `trust_solution`.
- **DLL анализаторов** должны приходить из глобальной папки NuGet-пакетов пользователя, каталога установки dotnet SDK или собственных `bin`/`obj` решения. Другие пути пропускаются.

Используйте инструменты `list_trusted_paths` и `revoke_trust` для просмотра и управления состоянием доверия. Постоянное доверие хранится в `%APPDATA%\roslyn-codelens\trust.json`.

Полная модель угроз — в [SECURITY.md](SECURITY.md).

## Быстрый старт

### npx (любой MCP-клиент)

```json
{
  "mcpServers": {
    "roslyn-codelens": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "roslyn-codelens-mcp"]
    }
  }
}
```

npm-пакет не содержит кода сервера — это лаунчер, который устанавливает .NET global tool `RoslynCodeLens.Mcp` соответствующей версии и запускает его, поэтому **.NET 10 SDK должен быть в `PATH`**.
Последующие запуски полностью пропускают установку и работают офлайн.

### VS Code / Visual Studio (через dnx)

Добавьте в ваши MCP-настройки (`.vscode/mcp.json` или настройки VS):

```json
{
  "servers": {
    "roslyn-codelens": {
      "type": "stdio",
      "command": "dnx",
      "args": ["RoslynCodeLens.Mcp", "--yes"]
    }
  }
}
```

### Плагин Claude Code

```bash
claude install gh:MarcelRoozekrans/roslyn-codelens-mcp
```

Плагину нужен только .NET 10 SDK: он запускает сервер через `dnx`, закреплённый к версии плагина, поэтому нет глобального инструмента, который нужно устанавливать или обновлять.

### .NET Global Tool

```bash
dotnet tool install -g RoslynCodeLens.Mcp
```

Затем добавьте в конфиг вашего MCP-клиента:

```json
{
  "mcpServers": {
    "roslyn-codelens": {
      "command": "roslyn-codelens-mcp",
      "args": [],
      "transport": "stdio"
    }
  }
}
```

### Docker

Работает без .NET SDK на хосте; решение bind-монтируется в `/workspace`.

```bash
docker build -t roslyn-codelens-mcp .
docker run -i --rm -v "$PWD:/workspace" roslyn-codelens-mcp
```

Смонтированное решение должно быть восстановлено (restore), чтобы `MSBuildWorkspace` мог разрешить его ссылки, а вывод инструментов отчитывается путями контейнера, а не хоста — см.
[docs/site/docs/getting-started/docker.md](docs/site/docs/getting-started/docker.md).

## Использование

Сервер автоматически находит файлы `.sln`, поднимаясь вверх от текущего каталога. Также можно передать один или несколько путей к решениям напрямую:

```bash
# Одно решение
roslyn-codelens-mcp /path/to/MySolution.sln

# Несколько решений — переключайтесь между ними через set_active_solution
roslyn-codelens-mcp /path/to/A.sln /path/to/B.sln
```

Когда загружено несколько решений, используйте `list_solutions`, чтобы увидеть, что доступно, и `set_active_solution("B")`, чтобы переключить контекст. Первый путь активен по умолчанию.

### HTTP-транспорт

По умолчанию сервер работает через stdio. Передайте `--http`, чтобы вынести те же инструменты через
[streamable HTTP](https://modelcontextprotocol.io/docs/concepts/transports) — полезно для одного долгоживущего сервера (тёплый Roslyn workspace, нет стоимости запуска на клиента) общего для нескольких локальных MCP-клиентов:

```bash
roslyn-codelens-mcp /path/to/MySolution.sln --http            # http://127.0.0.1:3001
roslyn-codelens-mcp /path/to/MySolution.sln --http --port 8080
```

```json
{
  "mcpServers": {
    "roslyn-codelens": {
      "type": "http",
      "url": "http://127.0.0.1:3001"
    }
  }
}
```

HTTP-эндпоинт привязывается только к `127.0.0.1` и предназначен для **однопользовательского локального использования**: все подключённые клиенты разделяют одно состояние решений (`set_active_solution` влияет на всех), и аутентификации нет. `--host` может расширить привязку, но сервер предупредит — его инструменты могут читать и изменять исходные файлы, поэтому делайте это только в сетях, которым полностью доверяете.

## Производительность

Все поиски типов используют предпостроенные обратные карты наследования, индексы членов и индексы атрибутов для доступа O(1). Замерено на i9-12900HK с .NET 10.0.7:

| Инструмент | Задержка | Память |
|------|--------:|-------:|
| `go_to_definition` | 2.1 µs | 576 B |
| `find_implementations` | 2.5 µs | 720 B |
| `get_project_dependencies` | 2.8 µs | 1.5 KB |
| `get_type_hierarchy` | 3.5 µs | 1.3 KB |
| `find_circular_dependencies` | 3.7 µs | 2.7 KB |
| `get_symbol_context` | 4.1 µs | 1.0 KB |
| `get_source_generators` | 16 µs | 23 KB |
| `analyze_data_flow` | 19 µs | 1.6 KB |
| `find_attribute_usages` | 72 µs | 904 B |
| `get_generated_code` | 78 µs | 24 KB |
| `analyze_control_flow` | 115 µs | 14 KB |
| `inspect_external_assembly` (summary) | 159 µs | 35 KB |
| `find_large_classes` | 265 µs | 3.5 KB |
| `get_di_registrations` | 478 µs | 16 KB |
| `inspect_external_assembly` (namespace) | 564 µs | 259 KB |
| `find_reflection_usage` | 705 µs | 19 KB |
| `get_complexity_metrics` | 781 µs | 25 KB |
| `get_code_actions` | 792 µs | 54 KB |
| `get_file_overview` | 797 µs | 101 KB |
| `get_diagnostics` | 822 µs | 99 KB |
| `get_nuget_dependencies` | 849 µs | 48 KB |
| `get_public_api_surface` | 885 µs | 247 KB |
| `get_type_overview` | 1.1 ms | 104 KB |
| `peek_il` | 1.1 ms | 34 KB |
| `find_disposable_misuse` | 3.5 ms | 286 KB |
| `find_uncovered_symbols` | 3.8 ms | 224 KB |
| `search_symbols` | 3.9 ms | 557 KB |
| `analyze_method` | 5.8 ms | 333 KB |
| `find_async_violations` | 7.0 ms | 335 KB |
| `find_tests_for_symbol` (direct) | 8.4 ms | 396 KB |
| `find_callers` | 10 ms | 337 KB |
| `find_tests_for_symbol` (transitive) | 12 ms | 399 KB |
| `find_naming_violations` | 15 ms | 788 KB |
| `find_unused_symbols` | 23 ms | 1.0 MB |
| `find_references` | 28 ms | 1013 KB |
| `analyze_change_impact` | 33 ms | 1.3 MB |
| Загрузка решения (одноразово) | ~4.1 s | 16 MB |

## Горячая перезагрузка

Сервер следит за изменениями в файлах `.cs`, `.csproj`, `.props` и `.targets`. При обнаружении изменения затронутые проекты лениво перекомпилируются при следующем запросе инструмента — перекомпилируются только устаревшие проекты и их downstream-зависимые, а не всё решение целиком.

Инструменты, возвращающие местоположение, включают флаг `IsGenerated`, чтобы отличать вывод source-генераторов от рукописного кода.

## Требования

- .NET 10 SDK
- .NET-решение с компилируемыми проектами

## Совместимость проектов

Сервер анализирует каждый проект, который MSBuildWorkspace может загрузить в рантайме .NET SDK.

**Поддерживается:** SDK-стиль проекты (`<Project Sdk="...">`), любой целевой фреймворк — `net48`, `net6.0`, `net8.0`, `net10.0` и т.д. Целевые .NET Framework работают без проблем *при условии, что csproj использует SDK-стиль формат*.

**Пропускаются (с предупреждением, а не крашем):** legacy проекты не в SDK-стиле (`<Project ToolsVersion="..." xmlns="http://schemas.microsoft.com/developer/msbuild/2003">`, обычно `.NET Framework` проекты, созданные в старых версиях Visual Studio). Они полагаются на импорты `Microsoft.Common.props` из .NET Framework MSBuild, поставляемого с Visual Studio, который недоступен в рантайме .NET SDK MSBuild.

Когда решение содержит legacy-проекты, сервер:

1. Загружает каждый SDK-стиль проект как обычно — все инструменты работают для них.
2. Пропускает каждый legacy-проект и записывает его в `LoadedSolution.SkippedProjects`.
3. Выводит список пропущенных через `list_solutions` (массив `SkippedProjects` в каждом `SolutionInfo`) и в возвращаемом сообщении `load_solution`. Каждая запись включает имя проекта, вид (`Legacy`) и причину.

**Чтобы анализировать legacy-проект,** конвертируйте его в SDK-стиль формат (см. [гид по миграции от Microsoft](https://learn.microsoft.com/en-us/dotnet/core/porting/project-structure)) или откройте решение из Visual Studio Developer Command Prompt, чтобы полный Visual Studio MSBuild был в `PATH`.

## Разработка

```bash
dotnet build
dotnet test
dotnet run --project benchmarks/RoslynCodeLens.Benchmarks -c Release
```

## Лицензии сторонних компонентов

- [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) — MIT-лицензия (v8+). Используется для дизассемблирования IL в инструменте `peek_il`.

## Семейство

Roslyn CodeLens MCP — часть небольшого семейства .NET-проектов для работы с ИИ:

- [AI.Sentinel](https://marcelroozekrans.github.io/AI.Sentinel/): Средство мониторинга безопасности для `IChatClient`: обнаружение prompt injection, галлюцинаций и PII с журналом аудита.
- [Minos.NET](https://marcelroozekrans.github.io/Minos.NET): Типизированные, source-генерированные вопросы решений с калиброванными ответами.
- [Rag.NET](https://marcelroozekrans.github.io/Rag.NET/): Модульный конвейер retrieval-augmented generation на Microsoft.Extensions.AI.
- [MemoryLens MCP](https://github.com/MarcelRoozekrans/memorylens-mcp): Профилирование памяти .NET по требованию с исправлениями, применимыми ИИ, через MCP.

## Построено на ZeroAlloc

Roslyn CodeLens MCP использует эти библиотеки [ZeroAlloc](https://zeroalloc.net), которые являются source-генераторами и анализаторами без рефлексии в рантайме:

- [ZeroAlloc.Analyzers](https://analyzers.zeroalloc.net)

## Лицензия

MIT
