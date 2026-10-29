# Правило: roslyn-codelens в Code-режиме (усиление)

Глобальная часть — `.roo/rules/02-roslyn-codelens.md` (область применения,
таблица «вопрос → инструмент», жизненный цикл, грабли). Здесь — только
дополнительные обязательства режима code.

## Перед любой правкой C#-кода

1. **Оценить blast radius**: `analyze_change_impact` для каждого символа,
   чья сигнатура, имя, поведение или наличие меняется. Для транзитивных
   вызывающих — `get_call_graph` (`direction: callers`).
2. Переименования и изменения сигнатур делать **только** через
   `rename_symbol` / `change_signature`, а не ручными заменами: они каскадно
   обновляют ссылки, конструкторы, overrides, `nameof` и XML-doc `cref`,
   включая интерфейс → реализации.
3. Проверить, чем правка будет прикрыта: `get_test_summary` — какие тесты
   ссылаются на изменяемые символы (поле `referencedSymbols`); для сложных
   мест — `find_uncovered_symbols`.
4. Для неочевидных правок заранее зафиксировать состояние: `git status` +
   `git diff` (изменяющие MCP-инструменты пишут на диск без подтверждения).

## Протокол изменяющих MCP-инструментов

Для `rename_symbol`, `change_signature`, `apply_code_action`,
`apply_fix`, `extract_method`, `format_document`:

1. Вызов с `preview: true` (по умолчанию) → изучить `edits` и `conflicts`.
2. Есть Conflicts (новые ошибки компилятора) → править план, **не применять**;
   `force: true` только по явному решению пользователя.
3. Убедиться, что дерево git чистое (или содержит только известные правки).
4. Применять `preview: false`; после — немедленно `get_diagnostics`
   (`severity: error`) по затронутым проектам.
5. Если правок много или они кросс-проектные — подтверждение пользователя
   до применения.

Изменяющие инструменты пишут на диск в обход ограничений режимов;
`format_document` / `apply_fix` / `extract_method` в `alwaysAllow`
`.roo/mcp.json` **не добавлять**.

## После правок

1. Один вызов `validate_edit` — диагностики по затронутым файлам плюс висячие
   ссылки по затронутым символам (вместо трёх вызовов `get_diagnostics` +
   `get_file_overview` + `find_references`). Fallback для файла вне решения —
   `get_diagnostics` с `filePath`.
2. Правки затронули `.csproj` / `Directory.Build.props` / NuGet / добавили
   новый файл → `rebuild_solution` (или `start_background_task`). Правки
   **только `.cs`** полного reload не требуют: сервер делает
   инкрементальный rebuild сам (`FileChangeTracker` + `RebuildIfStale`),
   а после правок самого MCP снапшот обновляется через `CommitAsync`.
3. Тесты — по `.roo/rules-code/01-test.md`:
   `dotnet test RoslynCodeLens.slnx`.
4. Затронут публичный API? → `get_public_api_surface` (baseline) и
   `find_breaking_changes` при наличии предыдущего снимка. Помнить:
   return-типы, `sealed` и nullable-аннотации инструмент не видит —
   проверять `get_overloads` / `get_method_source` вручную.
5. Архитектурные правки (слои, зависимости проектов) → `check_architecture`
   и `find_circular_dependencies` для подтверждения результата.

## Запрещено в code-режиме

- Менять C#-символы вслепую текстовым search/replace, не вызвав
  `find_references` / `analyze_change_impact`.
- **Гадать об API Roslyn-типов** (`CodeAction`, `TextLine`, `SyntaxNode` и т. п.):
  прежде чем писать код, опираясь на свойства/члены такого типа, — вызов
  `get_type_overview` или `get_symbol_context`.
- **Гадать о сигнатурах методов**: прежде чем писать вызов метода
  (особенно Roslyn/BCL) — `get_method_source` / `get_overloads` для точной
  сигнатуры (типы параметров, возвращаемый тип).
- **Писать тесты на поведение Roslyn-провайдера по догадке**: прежде чем
  закладывать ожидаемый результат в тест — `apply_code_action` /
  `extract_method` / `apply_fix` с `preview: true` на фикстуре, чтобы увидеть
  фактический результат правки.
- **Матчить code actions по локализованному заголовку** (`title`): заголовки
  зависят от культуры (ru-RU — «Извлечь метод»). Идентификация — по
  `equivalenceKey` (поле `get_code_actions`, `CodeAction.EquivalenceKey`),
  например `Extract_method`.
- Применять `rename_symbol` / `change_signature` с `preview: false` без
  проверки `conflicts` и без чистого дерева git.
- Удалять «неиспользуемый» код по одному лишь `find_unused_symbols` без
  проверки `find_references`.
- Игнорировать `get_diagnostics` после правок («должно компилироваться»).
- Закрывать задачу правки `.cs` **без** диагностики затронутого проекта —
  даже если `dotnet build` не запускался. Нет диагностики = правка не
  завершена.
- Искать **имя символа** через `search_files`, если файл входит в решение.
  Исключение — литералы, комментарии, XML-doc, конфиги; тогда это
  оговаривается явно.
- Вызывать `rebuild_solution` синхронно посреди длинного анализа без
  необходимости (переоткрывает и перекомпилирует всё решение) и после
  каждой правки `.cs` (инкрементальный rebuild сервер делает сам).
