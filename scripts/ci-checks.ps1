<#
.SYNOPSIS
    Repo-specific build checks, run by the shared deploy.yml after the unit tests.

.DESCRIPTION
    Runs from the repo root on ubuntu-latest, after `dotnet build *.slnx -c Release`.
    1. dotnet format --verify-no-changes (formatting drift fails the build)
    2. The E2EAPI tier: in-process on the fakes, no Azurite or browser needed.
    Integration (Azurite) and E2EUI (Playwright + microphone fixture) stay local: scripts/setup.ps1.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Host "::group::$Name"
    & $Command
    $code = $LASTEXITCODE
    Write-Host '::endgroup::'
    if ($code -ne 0) { Write-Host "::error::$Name failed (exit $code)"; exit $code }
}

Invoke-Step 'Formatting' { dotnet format PoFightJudge.slnx --verify-no-changes --no-restore }
Invoke-Step 'API tests (E2EAPI)' { dotnet test tests/PoFightJudge.E2EAPI -c Release --no-build --verbosity minimal }

Write-Host 'ci-checks: all passed.'
