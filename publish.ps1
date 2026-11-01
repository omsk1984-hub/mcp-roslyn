#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publish the RoslynCodeLens MCP server to ./publish for local MCP client use.

.DESCRIPTION
    One-shot local publish helper for the single server project of this
    repository (src/RoslynCodeLens). Performs, in order:

      1. remove the previous publish/ output (skipped with -SkipClean)
      2. dotnet restore (skipped with -NoRestore)
      3. dotnet publish src/RoslynCodeLens/RoslynCodeLens.csproj -c <Configuration> -o publish
      4. verify publish/RoslynCodeLens.dll exists
      5. print the MCP client config snippet pointing at the published DLL

    The output directory `publish/` is gitignored (.gitignore) - it is build
    output, not source. The MCP client launches the server from this path,
    e.g. "command": "dotnet", "args": ["<repo>/publish/RoslynCodeLens.dll"].

    The publish is framework-dependent (no -r): the machine needs the .NET 10
    runtime/SDK, which is the same prerequisite as running the server from
    source. Pass nothing else - the server finds the solution to analyze from
    its working directory or from positional arguments.

    If a server is currently running from publish/ (launched by an MCP client),
    the clean/publish steps can fail on Windows with MSB3021 "being used by
    another process". Stop the MCP client (or the RoslynCodeLens process)
    first.

    Every step fails fast: a non-zero exit code aborts the script with the
    same code, so CI and agents can rely on $LASTEXITCODE.

    Messages are ASCII-only on purpose: the terminal hosting this script may
    run in CP866 (see .roo/rules-code/02-powershell.md), and Cyrillic would be
    mangled when the script is invoked as a child process.

.PREREQUISITES
    - .NET SDK 10 on PATH (`dotnet`).

.EXAMPLE
    # Canonical invocation (never ".\publish.ps1" - the .ps1 file association
    # on this machine opens Notepad instead of executing):
    pwsh -NoProfile -ExecutionPolicy Bypass -File publish.ps1

.EXAMPLE
    pwsh -NoProfile -ExecutionPolicy Bypass -File publish.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    # Build configuration. Release matches CI and the canonical test command.
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    # Skip removing the previous publish/ output (faster, but stale files
    # from an older publish may remain).
    [switch]$SkipClean,

    # Skip `dotnet restore` (faster when packages are already restored).
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

# The script lives in the repository root, so $PSScriptRoot is the root itself.
$RepoRoot = $PSScriptRoot
$Project  = Join-Path $RepoRoot "src/RoslynCodeLens/RoslynCodeLens.csproj"
$OutDir   = Join-Path $RepoRoot "publish"
$EntryDll = Join-Path $OutDir "RoslynCodeLens.dll"

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)

    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    # Reset so a pure-PowerShell step (no native exe) does not inherit a stale
    # exit code from a previous step.
    $LASTEXITCODE = $null
    & $Body
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        Write-Host "$Name FAILED (exit $LASTEXITCODE)." -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "$Name OK." -ForegroundColor Green
}

Push-Location $RepoRoot
try {
    if (-not (Test-Path $Project)) {
        Write-Error "Project not found: $Project"
        exit 1
    }

    if (-not $SkipClean) {
        Invoke-Step "clean publish/" {
            if (Test-Path $OutDir) {
                Remove-Item $OutDir -Recurse -Force
            }
        }
    }

    if (-not $NoRestore) {
        Invoke-Step "dotnet restore" {
            dotnet restore $Project --nologo
        }
    }

    $publishArgs = @("publish", $Project, "-c", $Configuration, "-o", $OutDir, "--nologo")
    if ($NoRestore) {
        $publishArgs += "--no-restore"
    }

    Invoke-Step "dotnet publish ($Configuration -> publish/)" {
        dotnet @publishArgs
    }

    if (-not (Test-Path $EntryDll)) {
        Write-Error "Publish output missing: $EntryDll"
        exit 1
    }

    Write-Host ""
    Write-Host "Publish finished successfully." -ForegroundColor Green
    Write-Host ""
    Write-Host "MCP client config (stdio transport):"
    # JSON requires forward slashes (or escaped backslashes) in string values.
    $EntryDllJson = $EntryDll -replace '\\', '/'
    Write-Host @"
    {
      "mcpServers": {
        "roslyn-codelens": {
          "command": "dotnet",
          "args": ["$EntryDllJson"]
        }
      }
    }
"@
}
finally {
    Pop-Location
}
