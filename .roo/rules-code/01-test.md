- все тесты выполняй через `dotnet test` для решения проекта:
  - `dotnet test src/LLMNetHelper/LLMNetHelper.slnx`
  - точечно: `dotnet test src/LLMNetHelper/LLMNetHelper.Tests/LLMNetHelper.Tests.csproj`
  - SK-паритет: `dotnet test src/LLMCore/LLMCore.Orchestration.SemanticKernel.Tests/LLMCore.Orchestration.SemanticKernel.Tests.csproj`
  - либо через раннер: `run_LLMNetHelper.ps1 -Test`
- все тесты офлайн: сеть и Docker для их прохода не требуются
- исключения с сетью помечены трейтом `Category=Integration` и по умолчанию
  имеют статус **Skipped** (не Failed):
  - GitLab-интеграция (`LLMNetHelper.Tests/GitLab/GitLabIntegrationTests.cs`) —
    выполняется только при `GITLAB__INTEGRATION=1` и токене `GITLAB__TOKEN`
    (из окружения или `src/LLMNetHelper/.env`):
    `$env:GITLAB__INTEGRATION='1'; dotnet test src/LLMNetHelper/LLMNetHelper.slnx --filter Category=Integration`
- запускай скрипты только через создание временных файлов (debug-скрипты)
