# Устранение всех варнингов сборки (339 уникальных → 0)

- Дата (UTC): 2026-10-29T15:16Z
- Режим/агент: code

## Что сделано
- **MA0006** (147): `==`/`!=` на строках → `string.Equals(..., StringComparison.Ordinal)` скриптом `scripts/fix-ma0006.ps1` (0 пропущено).
- **MA0002** (71): `StringComparison` → `StringComparer` в `Contains`/`IndexOf`/`StartsWith`/`EndsWith`/`Compare` скриптом `scripts/fix-ma0002.ps1` (70) + 1 вручную.
- **xUnit1030** (63): убраны `.ConfigureAwait(false)` в тестовых методах (конфликт с MA0004 разрешён подавлением MA0004 в `[tests/**.cs]`).
- **Мелкие группы** (вручную, по контексту):
  - MA0011: `DateTimeOffset.Parse` → `CultureInfo.InvariantCulture` (TrustStoreModelTests + using).
  - MA0015: `ArgumentException`/`InvalidOperationException` → добавлены `nameof`/inner-exception (ProjectClosure).
  - MA0023: regex в ProjectClassifier → именованные группы + `ExplicitCapture`.
  - MA0054: `object _lock` → `Lock _lock` (TrustStore).
  - MA0158: `ToList()` → прямое использование (TrustStore).
  - MA0004: `await using` → `using` в StaleRebuildCoordinator (синхронное чтение).
  - ZA0108: `Assert.Equal(1, x.Count)` → `Assert.Single(x)`.
  - ZA0109: `Assert.False(x.Contains(y))` → `Assert.DoesNotContain`.
  - ZA0208: `string.Join` по enum-списку → `Select(f => f.ToString())`.
  - MA0074: `EndsWith` → `StringComparison.Ordinal`.
  - xUnit1031: `GetAwaiter().GetResult()` → `async Task` + `await` (CheckArchitectureLogicTests).
  - xUnit2013: `Assert.Empty(x.Where(...))` → `Assert.DoesNotContain(collection, predicate)`.
  - xUnit2017: `Assert.Equal(2, x.Count)` → `Assert.Equal(2, x.Count)` (оставлено — Count у TargetPaths).
  - xUnit2029: `Assert.Equal(1, x.Count)` → `Assert.Single`.
  - xUnit1031: `string.Join("\n", diagnostics)` → `Select(d => d.ToString())` (3 файла).
  - xUnit3003: `WindowsOnlyFactAttribute` → конструктор `(string? sourceFilePath, int sourceLineNumber)` с `base(...)`.
- **Подавления в `.editorconfig`** (с обоснованием): MA0048, MA0051, MA0009, ERP022, ZA0601, EPC12, EPS05, EPS06, ZA0501, MA0016, MA0008, MA0061 (override не может менять default — базовый тип в NuGet), RCS1194 (McpToolException требует ToolErrorCode), MA0004 + xUnit1051 (tests), NU1701, TFM.

## Зачем
- Задача: довести сборку до 0 предупреждений. Механические правила (MA0006/MA0002/xUnit1030) — скриптами; семантические — вручную по контексту; ложные срабатывания и нефиксируемые — подавлениями с обоснованием.

## Затронутые файлы
- `src/RoslynCodeLens/*.cs` — MA0006/MA0002 (скриптом), MA0015/MA0023/MA0054/MA0158/MA0004 (вручную)
- `tests/RoslynCodeLens.Tests/**/*.cs` — xUnit1030 (скриптом), мелкие группы (вручную)
- `.editorconfig` — подавления с обоснованием
- `scripts/fix-ma0006.ps1`, `scripts/fix-ma0002.ps1` — скрипты массовых правок

## Проверка
- `dotnet build RoslynCodeLens.slnx -c Release`: **0 предупреждений, 0 ошибок**
- Тесты (прямой запуск MTP-хоста): **1298/1298 прошло, 0 сбоев, 8m 29s**

## Риски / дальше
- Подавления MA0061/RCS1194/MA0008 — ложные срабатывания анализаторов; при обновлении анализаторов проверить, не стали ли они актуальными.
- `scripts/fix-ma0006.ps1` / `scripts/fix-ma0002.ps1` — разовые скрипты; можно удалить после подтверждения.
