# План: обновление всех NuGet-библиотек на актуальные .NET 10 версии

## Контекст и ключевой вывод

**TFM всех проектов уже `net10.0`** — «перевод на .NET 10» на уровне целевых
фреймворков уже выполнен:

- [`src/RoslynCodeLens/RoslynCodeLens.csproj`](src/RoslynCodeLens/RoslynCodeLens.csproj:4) — net10.0
- [`tests/RoslynCodeLens.Tests/RoslynCodeLens.Tests.csproj`](tests/RoslynCodeLens.Tests/RoslynCodeLens.Tests.csproj:4) — net10.0
- [`benchmarks/RoslynCodeLens.Benchmarks/RoslynCodeLens.Benchmarks.csproj`](benchmarks/RoslynCodeLens.Benchmarks/RoslynCodeLens.Benchmarks.csproj:5) — net10.0
- [`tools/DocGen/DocGen.csproj`](tools/DocGen/DocGen.csproj:4) — net10.0
- Все тестовые фикстуры — net10.0, **кроме**
  [`LegacyLib.csproj`](tests/RoslynCodeLens.Tests/Fixtures/LegacySolution/LegacyLib/LegacyLib.csproj:18)
  (v4.6.2 — **намеренный** фикстура для проверки legacy-обработки, не трогать).

Следовательно, задача = **обновить NuGet-зависимости до актуальных версий,
совместимых с .NET 10**, с сохранением зелёного билда и тестов. TFM менять не нужно.

## Текущие версии (исходная точка)

| Группа | Пакеты | Версия | Где |
|---|---|---|---|
| Roslyn | `Microsoft.CodeAnalysis.Workspaces.MSBuild`, `CSharp.Workspaces`, `Features`, `CSharp.Features` | 5.9.0 | src |
| MSBuild | `Microsoft.Build`, `.Framework`, `.Tasks.Core`, `.Utilities.Core`, `Microsoft.NET.StringTools` | 18.11.0 | src, benchmarks |
| MSBuild.Locator | `Microsoft.Build.Locator` | 1.11.2 | src, benchmarks |
| MCP | `ModelContextProtocol`, `ModelContextProtocol.AspNetCore` | 2.3.0 | src |
| Decompiler | `ICSharpCode.Decompiler` | 11.2.0.10050 | src |
| Тесты | `Microsoft.NET.Test.Sdk` | 18.11.0 | tests |
| Тесты | `xunit` | 2.9.3 | tests |
| Тесты | `xunit.runner.visualstudio` | 4.0.1 | tests |
| Тесты | `coverlet.collector` | 10.1.0 | tests |
| Бенчмарки | `BenchmarkDotNet` | 0.15.8 | benchmarks |
| Анализаторы | `Meziantou.Analyzer` | 3.0.317 | все (props) |
| Анализаторы | `Roslynator.Analyzers` | 5.0.1 | все (props) |
| Анализаторы | `ErrorProne.NET.CoreAnalyzers`, `.Structs` | 0.1.2 | все (props) |
| Анализаторы | `ZeroAlloc.Analyzers` | 1.11.3 | все (props) |

Анализаторы объявлены в [`Directory.Build.props`](Directory.Build.props:3) и
применяются ко всем проектам.

## Ключевые риски и связки

1. **Roslyn ↔ MSBuild ↔ SDK.** `Microsoft.CodeAnalysis.Workspaces.MSBuild`
   работает с MSBuild из установленного SDK (через `MSBuild.Locator`).
   Compile-time `Microsoft.Build.*` (с `ExcludeAssets=runtime`) должны
   совпадать с версией MSBuild SDK, иначе — несовпадение типов. На .NET 10 SDK
   это MSBuild 18.x. **Roslyn и MSBuild обновлять вместе** и тестировать вместе.
2. **Roslyn определяет язык C# и code actions.** Апгрейд Roslyn меняет
   поддерживаемый C# и поведение рефакторингов → обязателен **полный** прогон
   тестов после этой группы.
3. **xunit v2 → v3 — breaking** (атрибуты, runner, API). По умолчанию
   **оставляем v2** (только патч-обновления); v3 — отдельное решение.
4. **BenchmarkDotNet** — возможны major-бампы с API-изменениями → проверить
   перед применением.
5. **Dockerfile уже на `mcr.microsoft.com/dotnet/sdk:11.0`**
   ([`Dockerfile:15`](Dockerfile:15)), а TFM net10.0 — непротиворечиво (SDK 11
   собирает net10.0), но стоит решить: оставить 11 или выровнять.
6. **CI** `dotnet-version: 10.0.x`
   ([`.github/workflows/ci.yml:26`](.github/workflows/ci.yml:26)) — корректно
   для net10.0, менять не нужно.
7. **`tools/DocGen` не входит в `RoslynCodeLens.slnx`** (в решении 3 проекта).
   `dotnet test RoslynCodeLens.slnx` его не собирает — DocGen проверять
   отдельным `dotnet build tools/DocGen/DocGen.csproj`.

## Порядок выполнения (по группам, с проверкой после каждой)

Каждая группа: правка версии → `dotnet build` → `dotnet test RoslynCodeLens.slnx`
→ только при зелёном — следующая группа.

1. **Базовая линия** — `dotnet build` + `dotnet test RoslynCodeLens.slnx`
   (зелёный старт до правок).
2. **Обнаружение версий** — `dotnet list package --outdated` по src, tests,
   benchmarks, tools/DocGen; зафиксировать целевые версии (точные номера
   определяются в момент выполнения, не угадывать).
3. **Roslyn** — 4 пакета в lockstep → build + test.
4. **MSBuild** — 5 пакетов + `Microsoft.Build.Locator` → build + test.
5. **MCP** — 2 пакета в lockstep → build + test.
6. **ICSharpCode.Decompiler** → build + test.
7. **Тестовые пакеты** — `Microsoft.NET.Test.Sdk`, `xunit`,
   `xunit.runner.visualstudio`, `coverlet.collector` → build + test.
8. **Анализаторы** — 4 пакета в `Directory.Build.props` → build + test.
9. **BenchmarkDotNet** — проверить breaking changes → build + test.
10. **MCP-rebuild** — `rebuild_solution` + `get_diagnostics` (правки csproj
    требуют полного reload; правки только `.cs` — не требуют).
11. **Доки/CI/Docker** — обновить упоминания версий в `README.md`,
    `README_ru.md`, `docs/site/docs/*` и `Dockerfile` при необходимости.
12. **Финал** — полный `dotnet test RoslynCodeLens.slnx` +
    `dotnet build tools/DocGen/DocGen.csproj` + smoke-запуск сервера.

## Точки решения (нужно подтверждение)

- **xunit v3**: мигрировать или оставить v2? (рекомендация: оставить v2)
- **BenchmarkDotNet major**: брать major или остаться на 0.15.x?
- **Docker SDK**: оставить `sdk:11.0` или выровнять на `10.0`?
