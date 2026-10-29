# Исправление двух предупреждений сборки Release (xUnit3003, ZA0109)

- Дата (UTC): 2026-10-29T15:40Z
- Режим/агент: code

## Что сделано
- `SolutionChangeWriterTests.WindowsOnlyFactAttribute`: параметр `sourceFilePath` получил
  `[CallerFilePath]`, `sourceLineNumber` — `[CallerLineNumber]` (плюс `using System.Runtime.CompilerServices;`).
  xUnit3003 требовал публичный конструктор source-information; конструктор уже существовал, но без
  caller-атрибутов Roslyn-анализатор не считал его валидным источником `SourceFilePath`/`SourceLineNumber`.
- `Tools/GetFileOverviewToolTests.cs:54`: `[InlineData("Index.cshtml", new string[0], ...)]` —
  добавлен точечный `#pragma warning disable ZA0109` с комментарием. Массив нулевой длины здесь
  **предмет теста**, а не случайная аллокация: `Array.Empty<string>()` в аргументах атрибута
  невозможен (CS0182 — аргумент атрибута должен быть константным выражением).
- Подтверждено `dotnet build RoslynCodeLens.slnx --no-restore -c Release`: «Предупреждений: 0, Ошибок: 0».

## Зачем
- `scripts/rebuild.ps1` завершался с 2 warning'ами в тестовом проекте; цель — чистая сборка,
  чтобы предупреждения не маскировали будущие регрессии.
- Альтернатива «заменить `new string[0]` на `Array.Empty<string>()`» отклонена: некомпилируемо.
  Альтернатива «глобальный NoWarn ZA0109 в тестовом проекте» отклонена: теряет сигнал в остальных местах.

## Затронутые файлы
- `tests/RoslynCodeLens.Tests/SolutionChangeWriterTests.cs` — caller-атрибуты в конструкторе
  `WindowsOnlyFactAttribute` + `using System.Runtime.CompilerServices;`.
- `tests/RoslynCodeLens.Tests/Tools/GetFileOverviewToolTests.cs` — точечный `#pragma warning disable/restore ZA0109`.

## Проверка
- `get_diagnostics` (RoslynCodeLens.Tests, `severity: warning`, `includeAnalyzers: false`): 0 элементов.
- `get_diagnostics` (RoslynCodeLens.Tests, `severity: error`): 0 ошибок.
- `dotnet build RoslynCodeLens.slnx --no-restore -c Release`: успешно, предупреждений 0, ошибок 0.
- тесты: не запускались по явной инструкции пользователя («имправь варнинги без тестов»).
  Поведенческий риск минимален: изменения — атрибуты source-information xUnit (только метаданные
  теста) и pragma-директива.

## Риски / дальше
- `WindowsOnlyFactAttribute` теперь получает реальные `SourceFilePath`/`SourceLineNumber` места
  вызова; если в будущем появится логика, зависящая от этих значений, она станет осмысленной
  (раньше они были `null`/`-1`).
- Точечный `#pragma` в `GetFileOverviewToolTests` — единственное разумное решение для ZA0109 в
  аргументах атрибута; при апгрейде ZeroAlloc.Analyzers проверять, не появился ли fixer,
  предлагающий невалидный код.
- Полноценный прогон MTP-хоста (см. `.roo/rules-code/01-test.md`) остаётся рекомендуемой
  следующей проверкой перед коммитом.
