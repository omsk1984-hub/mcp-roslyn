# Кандидаты на удаление (2026-10-29)

Результат аудита «лишних файлов». Все файлы отслеживаются git (чистое дерево),
попавшие в коммит `9862287` вместе с worklog
[`worklog/2026-10-29T15-16-fix-all-warnings.md`](../worklog/2026-10-29T15-16-fix-all-warnings.md).
Ссылок на них из кода, скриптов, CI, правил и доков **не найдено**
(проверено `findstr`/поиском по содержимому).

## 1. Удалить — временные артефакты сессии «устранение варнингов» (339 → 0)

Логи сборок и дампы предупреждений, созданные при диагностике; задача закрыта
(сборка: 0 предупреждений, 0 ошибок; тесты 1298/1298).

| Файл | Что это |
|---|---|
| `build-final.log` | лог финальной сборки |
| `build-full.log` | полный лог сборки с варнингами |
| `build-ma0006.log` | лог сборки для анализа MA0006 |
| `build-warnings.log` | лог варнингов (TFM + анализаторы) |
| `build-warnings2.log` | лог варнингов, итерация 2 |
| `build-warnings3.log` | лог варнингов, итерация 3 |
| `build-warnings4.log` | лог варнингов, итерация 4 |
| `ma0002-cols.txt` | дамп MA0002 с колонками |
| `ma0002-dryrun.txt` | dry-run правок MA0002 |
| `ma0006-dryrun.txt` | dry-run правок MA0006 |
| `small-rules.txt` | дамп мелких групп правил (MA0011 и др.) |
| `warning-sections.txt` | дамп варнингов по секциям (MA0051 и др.) |
| `warning-data/` (29 файлов) | дампы варнингов по правилам (`warn-*.txt`, `*-lines.txt`) |

Команда удаления:

```
git rm build-final.log build-full.log build-ma0006.log build-warnings.log build-warnings2.log build-warnings3.log build-warnings4.log ma0002-cols.txt ma0002-dryrun.txt ma0006-dryrun.txt small-rules.txt warning-sections.txt
git rm -r warning-data
```

## 2. Требует решения — вероятно лишние, но не артефакты

| Файл | Комментарий |
|---|---|
| `icon.svg` | Иконка в SVG. В репозитории **не используется нигде** (CI, `npm/`, `plugins/`, `server.json`, `glama.json`, csproj — без ссылок). Кандидат на удаление. |
| `plans/1.md` | Исторический план 27 (edit quality) — дубль темы `plan/27_roslyn-codelens-server-feature-plan.md` (содержимое схоже, хеши различаются). По правилу `01-general.md` планы — исторические. |
| `plans/2.md` | Исторический план 2 (equivalence key). Актуальные планы — `plans/2026-10-28-solid-architecture-audit.md`, `plans/2026-10-29-dotnet10-library-upgrade.md`. |

## 3. Проверено — НЕ лишние (не удалять)

| Путь | Почему оставляем |
|---|---|
| `tools/DocGen/` | Используется CI: `.github/workflows/docs-build.yml`, `docs-deploy.yml` (`dotnet run --project tools/DocGen`) |
| `icon.png` | Иконка NuGet-пакета: `PackageIcon` + `Pack="true"` в `src/RoslynCodeLens/RoslynCodeLens.csproj` (упаковывается в `RoslynCodeLens.Mcp`) |
| `.config/dotnet-tools.json` | Manifiest dotnet-tools (GitVersion 6.8.2) |
| `worklog/` | Журнал работ по правилу `00-worklog.md` |
| `docs/plans/`, `plan/` | Исторические планы (часть истории проекта) |
| `glama.json`, `server.json` | Метаданные MCP-реестров (Glama, modelcontextprotocol.io) |
| `npm/`, `plugins/` | npm-лаунчер и плагин (часть дистрибуции) |
| `scripts/rebuild.ps1`, `scripts/delist-prereleases.ps1` | Активные скрипты (правила `01-test.md`, `02-powershell.md`) |
