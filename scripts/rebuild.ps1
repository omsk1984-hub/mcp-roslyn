#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Recompile the RoslynCodeLens solution (clean + restore + build), optionally
    followed by the MTP test host run.

.DESCRIPTION
    One-shot rebuild helper for the single solution of this repository
    (RoslynCodeLens.slnx: src, tests, benchmarks). Performs, in order:

      1. dotnet clean   (skipped with -SkipClean for an incremental build)
      2. dotnet restore
      3. dotnet build -c <Configuration> --no-restore
      4. optional: run the compiled test host directly (xunit v3 is MTP-native;
         `dotnet test` is NOT supported for MTP apps on the .NET 10 SDK, see
         .roo/rules-code/01-test.md). -Coverage adds the MTP CodeCoverage
         extension and writes TestResults/coverage.cobertura.xml.

    Every step fails fast: a non-zero exit code aborts the script with the
    same code, so CI and agents can rely on $LASTEXITCODE.

    Messages are ASCII-only on purpose: the terminal hosting this script may
    run in CP866 (see .roo/rules-code/02-powershell.md), and Cyrillic would
    be mangled when the script is invoked as a child process.

.PREREQUISITES
    - .NET SDK 10 on PATH (`dotnet`).

.EXAMPLE
    # Canonical invocation (never ".\rebuild.ps1" - the .ps1 file association
    # on this machine opens Notepad instead of executing):
    pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/rebuild.ps1

.EXAMPLE
    pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/rebuild.ps1 -Test -Coverage
#>
[CmdletBinding()]
param(
    # Build configuration. Release matches CI and the canonical test command.
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    # Skip `dotnet clean`: faster incremental rebuild.
    [switch]$SkipClean,

    # Run the MTP test host after a successful build.
    [switch]$Test,

    # Collect coverage via Microsoft.Testing.Extensions.CodeCoverage (implies -Test).
    [switch]$Coverage
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Solution = Join-Path $RepoRoot "RoslynCodeLens.slnx"
# TFM is pinned by the projects (net10.0); keep in sync with Directory.Build.props consumers.
$TestDll  = Join-Path $RepoRoot "tests/RoslynCodeLens.Tests/bin/$Configuration/net10.0/RoslynCodeLens.Tests.dll"

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)

    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -ne 0) {
        Write-Host "$Name FAILED (exit $LASTEXITCODE)." -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "$Name OK." -ForegroundColor Green
}

Push-Location $RepoRoot
try {
    if (-not (Test-Path $Solution)) {
        Write-Error "Solution not found: $Solution"
        exit 1
    }

    if (-not $SkipClean) {
        Invoke-Step "dotnet clean ($Configuration)" {
            dotnet clean $Solution -c $Configuration --nologo
        }
    }

    Invoke-Step "dotnet restore" {
        dotnet restore $Solution
    }

    Invoke-Step "dotnet build --no-restore -c $Configuration" {
        dotnet build $Solution -c $Configuration --no-restore --nologo
    }

    if ($Test -or $Coverage) {
        if (-not (Test-Path $TestDll)) {
            Write-Error "Test host not found: $TestDll (build did not produce it?)"
            exit 1
        }

        # Direct MTP host run - the canonical command from .roo/rules-code/01-test.md.
        # Exit codes: 0 = all passed, 5 = failures, other non-zero = host error.
        $testArgs = @($TestDll)
        if ($Coverage) {
            $testArgs += @(
                "--coverage",
                "--coverage-output-format", "cobertura",
                "--coverage-output", "coverage.cobertura.xml",
                "--results-directory", "./TestResults"
            )
        }

        Invoke-Step "test host ($Configuration$(if ($Coverage) { ', coverage' }))" {
            dotnet @testArgs
        }
    }

    Write-Host ""
    Write-Host "Rebuild finished successfully." -ForegroundColor Green
}
finally {
    Pop-Location
}
