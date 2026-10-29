# План 27, этапы E и G: `extract_method` + финализация документации

- Дата (UTC): 2026-10-28T14:51Z
- Режим/агент: code

## Что сделано

- **Этап E — `extract_method`** (коммит `34c561b`): тонкий фасад над
  `ExtractMethodCodeRefactoringProvider` из `Microsoft.CodeAnalysis.CSharp.Features`.
  Ключевые решения, отличные от плана:
  - Действие ищется по **нелокализованному** `CodeAction.EquivalenceKey`
    (`"Extract_method"`), а не по заголовку: на ru-RU-сборке Roslyn заголовок —
    «Извлечь метод» (подтверждено зондовым тестом и живым `get_code_actions`).
  - Проба показала: у extract-method **нет** `NestedActions` и он не
    `CodeActionWithOptions` (пункт плана E.1.3 неработоспособен в Roslyn 5.9).
    Провайдер сам называет метод `NewMethod`, поэтому запрошенное `methodName`
    применяется **отдельным `Renamer.RenameSymbolAsync`** уже к изменённому
    решению — до записи на диск.
  - Извлечённый метод находится диффом имён `MethodDeclarationSyntax`
    «до/после» (Roslyn гарантирует свежее имя), а не эвристикой по префиксу.
  - `CodeActionRunner.GetRawActionsAsync` — новый публичный доступ к сырым
    `CodeAction` (логика выбора действия по типу/ключу живёт в инструменте,
    а не в раннере).
  - Запись — только через `SolutionChangeSafety.PreviewOrApplyAsync`
    (preview по умолчанию, конфликты, свежесть, деградированная загрузка,
    коммит в снапшот).
- **Этап G — документация** (коммит `1a1ade1`): README (4 строки Features +
  скоуп `get_diagnostics` + trust-модель с `apply_fix`), `docs/features.md`
  (счётчик 54→71, таблица write-инструментов, секция `validate_edit`,
  фаза 6 завершена), `docs/BACKLOG.md` (Recently shipped + новый раздел
  отложенного), `.roo/rules/02-roslyn-codelens.md`,
  `.roo/rules-code/03-roslyn.md`, `.roo/skills/roslyn-codelens-usage/SKILL.md`
  (67→71, сняты оговорки «после плана 27», нюансы 10–11 про локализацию
  заголовков и `NewMethod`).

## Зачем

- План 27 закрывает отчёт о качестве правок: после `apply_diff` агент должен
  одним вызовом проверить файл (`validate_edit`), а точечные write-инструменты
  (`apply_fix`, `extract_method`, `format_document`) убирают ручные,
  хрупкие цепочки «заголовок → `apply_code_action`».
- Альтернатива «реализовать extract-method своим алгоритмом» отклонена:
  Roslyn уже корректно выводит параметры/возврат, свой вывод только
  рассинхронится с IDE.

## Затронутые файлы

- `src/RoslynCodeLens/CodeActionRunner.cs` — `GetRawActionsAsync`
- `src/RoslynCodeLens/Models/ExtractMethodResult.cs` — новая модель результата
- `src/RoslynCodeLens/Tools/ExtractMethodLogic.cs` — логика (equivalence-key,
  rename, write gate)
- `src/RoslynCodeLens/Tools/ExtractMethodTool.cs` — MCP-обёртка
- `tests/RoslynCodeLens.Tests/ExtractMethodLogicTests.cs` — 15 тестов
- `README.md`, `docs/features.md`, `docs/BACKLOG.md` — описание новых инструментов
- `.roo/rules/02-roslyn-codelens.md`, `.roo/rules-code/03-roslyn.md`,
  `.roo/skills/roslyn-codelens-usage/SKILL.md` — автопилот агента

## Проверка

- `get_diagnostics` (RoslynCodeLens, severity error): **0 ошибок** после правок.
- `dotnet test RoslynCodeLens.slnx` (полный прогон): **1239/1239 зелёные**
  (было 1207 до плана; +32 за этапы A/C/D/E), 0 пропущенных.
- `dotnet publish src/RoslynCodeLens -c Release -o publish`: успешно,
  `publish\RoslynCodeLens.dll` на месте (путь из `.roo/mcp.json`).

## Риски / дальше

- **Процесс MCP в этой сессии работает на старой сборке**: publish обновил DLL,
  но перезапуск сервера — только с явного разрешения пользователя (правило
  проекта). До перезапуска новые инструменты не видны клиенту.
- `format_document` / `apply_fix` / `extract_method` **не добавлять** в
  `alwaysAllow` `.roo/mcp.json` — они пишут на диск.
- Отложено в `docs/BACKLOG.md`: `extract_interface`, `encapsulate_field`,
  `add_member`, `move_member` (паттерн доказан), `writeBaselineTo` у
  `validate_edit`, `find_nullability_mismatches`.
- `extract_method` не поддерживает выделение внутри лямбд/локальных функций
  (диапазон обязан лежать в теле одного метода) — при несоответствии
  инструмент возвращает `InvalidArgument` со списком доступных действий.
