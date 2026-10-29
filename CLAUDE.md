# Roslyn CodeLens MCP

This is a Roslyn-based MCP server that provides semantic code intelligence for .NET codebases.

## MCP Server

The `.mcp.json` configures the roslyn-codelens MCP server to analyze this solution. It runs via `dotnet run` and connects over stdio.

## Skill

The `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` skill teaches Claude when and how to use the 67 code intelligence tools. Use the MCP tools instead of Grep/Glob for any .NET semantic queries (finding implementations, callers, references, diagnostics, etc.).

## Project Structure

- `src/RoslynCodeLens/` — MCP server (entry point: Program.cs)
- `tests/RoslynCodeLens.Tests/` — Unit tests (xUnit)
- `benchmarks/RoslynCodeLens.Benchmarks/` — BenchmarkDotNet performance tests
- `plugins/roslyn-codelens/` — Claude Code skill definition

## Development

- Build: `dotnet build -c Release`
- Test: прямой запуск MTP-хоста (xunit v3 на .NET 10 SDK не поддерживает `dotnet test`):
  `dotnet tests/RoslynCodeLens.Tests/bin/Release/net10.0/RoslynCodeLens.Tests.dll`
  (см. `.roo/rules-code/01-test.md`)
- Benchmarks: `dotnet run --project benchmarks/RoslynCodeLens.Benchmarks -c Release`
