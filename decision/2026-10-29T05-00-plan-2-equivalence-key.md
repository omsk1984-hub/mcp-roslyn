# План 2: возврат `equivalenceKey` в `get_code_actions` + правила + статусы плана

- Дата (UTC): 2026-10-29T05:00Z
- Режим/агент: code

## Что сделано
- Сверил [`plans/2.md`](../plans/2.md) с кодом: пункты 3–8 уже были реализованы,
  пункт 1 закрыт без параметра `includeNested` (`nestedActions` всегда в ответе
  рекурсивно; у extract-method в Roslyn 5.9 вложенных действий нет — см.
  `decision/2026-10-28T14-51-plan-27-edit-quality.md`).
- Пункт 2 реализован: `CodeActionInfo` получил опциональное поле
  `EquivalenceKey` (нелокализованный `CodeAction.EquivalenceKey`), проброшено в
  `CodeActionRunner.ToCodeActionInfo` (включая вложенные действия).
- Описание MCP-инструмента `get_code_actions` дополнено: `equivalenceKey`
  возвращается вместе с `title`, матчинг — по ключу, а не по локализованному
  заголовку.
- Правила `.roo/rules-code/03-roslyn.md`: в «Запрещено в code-режиме» добавлены
  четыре запрета из плана (гадание об API Roslyn-типов, гадание о сигнатурах,
  тесты по догадке без `preview: true`, матчинг code actions по `title`).
  Запрет 3 (текстовый поиск вместо семантического) уже присутствовал.
- `plans/2.md` обновлён: сводная таблица с колонкой «Статус (2026-10-29)»,
  приоритеты и рекомендации помечены выполненными.

## Зачем
- Агент 4–5 раз рассуждал вслух о вложенных действиях и локализованных
  заголовках, не имея фактов (кейсы 1 и 2 плана). `equivalenceKey` в ответе
  `get_code_actions` делает идентификацию действия стабильной на любой культуре.
- Альтернатива «параметр `includeNested`» отклонена: поле `NestedActions`
  уже всегда заполняется, флаг был бы мёртвым кодом.

## Затронутые файлы
- `src/RoslynCodeLens/Models/CodeActionInfo.cs` — поле `EquivalenceKey`
- `src/RoslynCodeLens/CodeActionRunner.cs` — `ToCodeActionInfo` пробрасывает ключ
- `src/RoslynCodeLens/Tools/GetCodeActionsTool.cs` — описание инструмента
- `tests/RoslynCodeLens.Tests/GetCodeActionsEquivalenceKeyTests.cs` — новый, 2 теста
- `.roo/rules-code/03-roslyn.md` — 4 новых запрета
- `plans/2.md` — статусы пунктов
- `README.md`, `README_ru.md` — строка `get_code_actions` дополнена про `equivalenceKey`
- `docs/features.md` — раздел 3.1: `get_code_actions` отчитывается о `equivalenceKey`
- `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` — пункт `get_code_actions`: матчинг по ключу
- `.roo/skills/roslyn-codelens-usage/SKILL.md` — «Нюанс 10»: `equivalenceKey` теперь в ответе `get_code_actions`

## Проверка
- get_diagnostics (RoslynCodeLens, RoslynCodeLens.Tests, severity error): 0 ошибок
- dotnet test RoslynCodeLens.slnx (полный прогон): 1298/1298 зелёные, 0 пропущенных

## Риски / дальше
- Публичный API `CodeActionInfo` расширен опциональным полем — обратно
  совместимо; `find_breaking_changes` по baseline не требуется (только добавление).
- Запущенный процесс MCP работает на старой сборке: новое поле в ответе
  `get_code_actions` появится после перезапуска сервера (только с разрешения
  пользователя).
