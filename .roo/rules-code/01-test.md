# Правило: запуск тестов (Code-режим) — прямой запуск MTP-хоста

## Каноническая команда

Все тесты выполняются **прямым запуском собранного тестового exe**, а не `dotnet test`:

```
dotnet build RoslynCodeLens.slnx -c Release
dotnet tests/RoslynCodeLens.Tests/bin/Release/net10.0/RoslynCodeLens.Tests.dll \
  --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml \
  --results-directory ./TestResults
```

Код возврата: `0` — все тесты прошли, `5` — есть падения, иное ненулевое — ошибка.

## Почему не `dotnet test`

- Тестовый проект мигрирован на **xunit v3**, который нативен для
  **Microsoft.Testing.Platform (MTP)**.
- На **.NET 10 SDK** таргет VSTest для MTP-приложений **больше не поддерживается**
  (`Microsoft.Testing.Platform.MSBuild.targets`:
  «Testing with VSTest target is no longer supported by Microsoft.Testing.Platform
  on .NET 10 SDK and later»).
- Opt-in нового `dotnet test` через `global.json` в SDK 10.0.401 не найден
  (проверено: свойство `test.useMTP` и другие кандидаты не действуют).
- Поэтому единственный рабочий путь — прямой запуск exe (MTP-хост).

## Покрытие (coverage)

- Расширение `Microsoft.Testing.Extensions.CodeCoverage` (MTP-native) регистрируется
  собственным entry point [`TestEntryPoint.cs`](../../tests/RoslynCodeLens.Tests/TestEntryPoint.cs),
  который всегда маршрутизирует через `TestPlatformTestFramework.RunAsync` с
  `SelfRegisteredExtensions` — поэтому `--coverage` доступен при прямом запуске
  (автогенерированный entry point xunit регистрирует расширения только на ветках
  `--server` / `--internal-msbuild-node`).
- Файл настроек `coverage.runsettings` **удалён**: он был специфичен для coverlet/VSTest.
  Дефолтное поведение MTP-coverage корректно: в отчёт попадает только продуктивная
  сборка `RoslynCodeLens` (тесты/бенчмарки исключены), строковое покрытие ~86%.

## Выборочный прогон

- `--filter-class 'Ns.Class'`, `--filter-method 'Ns.Class.Method'`,
  `--filter-namespace 'Ns*'`, `--filter-display-name '…'` (wildcard `*`).
- `--filter "<VSTest-синтаксис>"` — совместимость, одиночный.
- Простые / query / VSTest-фильтры **не смешиваются** между собой.

## Примечания

- `TestResults/` в `.gitignore`.
- Фикстуры (`tests/.../Fixtures/**`) — отдельные решения, в основной прогон не входят;
  при необходимости восстанавливать/собирать отдельно (см. CI `Restore test fixtures`).
