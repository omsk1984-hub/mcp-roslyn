# Скрипт перекомпиляции scripts/rebuild.ps1

- Дата (UTC): 2026-10-29T12:34Z
- Режим/агент: code

## Что сделано

- Добавлен [`scripts/rebuild.ps1`](../scripts/rebuild.ps1) — однострочный rebuild всего решения
  `RoslynCodeLens.slnx`: `dotnet clean` → `dotnet restore` → `dotnet build -c <Config> --no-restore`,
  опционально (`-Test`/`-Coverage`) — прямой прогон MTP-хоста тестов с cobertura-coverage
  (каноническая команда из `.roo/rules-code/01-test.md`).
- Флаги: `-Configuration` (Debug|Release, по умолчанию Release), `-SkipClean` (инкрементальная
  сборка без clean), `-Test`, `-Coverage` (подразумевает `-Test`). Fail-fast: ненулевой код
  возврата любого шага прерывает скрипт с тем же кодом.
- Сообщения скрипта — ASCII-only (терминал агентов может быть в CP866, кириллица в выводе
  дочерних процессов искажается; см. `.roo/rules-code/02-powershell.md`).
- Включён в документацию: `CLAUDE.md` (раздел Development) и
  `.roo/rules-code/01-test.md` (раздел «Примечания»).

## Зачем

- Запрос пользователя: «добавь скрипт для перекомпиляции». В `scripts/` был только
  `delist-prereleases.ps1` (NuGet-операция); рутинный цикл clean→restore→build→тесты
  не был автоматизирован, особенно с учётом нестандартного MTP-прогона тестов
  (`dotnet test` не работает на SDK 10).

## Затронутые файлы

- `scripts/rebuild.ps1` — новый файл.
- `CLAUDE.md` — пункт Rebuild в Development.
- `.roo/rules-code/01-test.md` — примечание о скрипте.

## Проверка

- Парсинг: `[Parser]::ParseFile` — 0 ошибок.
- Реальный прогон `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/rebuild.ps1 -SkipClean`:
  exit 0; restore OK; build Release — **0 ошибок** (696 предупреждений — существующие,
  не изменены); тестовый exe на месте (`bin/Release/net10.0/RoslynCodeLens.Tests.dll`).
- Ветка `-Test` повторяет каноническую команду CI (`.github/workflows/ci.yml`),
  MTP-хост проверен на месте (`--help` отвечает). Полный прогон тестов через скрипт
  не запускался (~16 мин) — код ветки идентичен проверенному в CI.
- C#-код не менялся → diagnostics/`validate_edit` не требуются.

## Риски / дальше

- Путь к тестовому exe содержит TFM `net10.0` буквально — при смене TFM проектов
  синхронно править `$TestDll` в скрипте.
- `-Coverage` на Linux CI не проверялся локально (наследует риск из
  `2026-10-29T12-18-dotnet10-mtp-workflow.md`).
