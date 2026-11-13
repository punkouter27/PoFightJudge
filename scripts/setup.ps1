#Requires -Version 7.0
<#
.SYNOPSIS
    First-run setup for PoFightJudge on a new machine.

.DESCRIPTION
    Walks a clean clone to a running app:

      1. Prerequisites (.NET 10 SDK, Docker Desktop, Azure CLI) — installed via winget when missing
      2. The ASP.NET Core development certificate, which Azurite needs before it will speak HTTPS
      3. Azurite (OAuth + HTTPS, ports 12000/12002) and Seq (5341) via docker compose
      4. az login, and a check that the PoFightJudge--* secrets exist in kv-poshared2 — names only, never values
      5. Restore, build (Release), Playwright's Chromium, and the whole test suite
      6. -Run: starts the API

    Nothing here writes to Azure. A missing secret is set in kv-poshared2 by hand.

.PARAMETER Run
    Start the API when setup finishes.

.PARAMETER SkipTests
    Restore and build, but do not run the suite.

.EXAMPLE
    ./SCRIPTS/setup.ps1
    ./SCRIPTS/setup.ps1 -Run -SkipTests
#>
[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Test-Command($Name) { $null -ne (Get-Command $Name -ErrorAction SilentlyContinue) }
function Write-Ok($Message) { Write-Host "OK: $Message" -ForegroundColor Green }
function Write-Todo($Message) { Write-Host "TODO: $Message" -ForegroundColor Yellow }
function Install-WingetPackage($Id, $Display) {
    Write-Host "Installing $Display..." -ForegroundColor Yellow
    winget install --id $Id --accept-package-agreements --accept-source-agreements --silent
}

Write-Host '=== PoFightJudge setup ===' -ForegroundColor Cyan

# ── 1. Prerequisites ────────────────────────────────────────────────────────────
if (-not (Test-Command winget)) { throw 'winget is required (install App Installer from the Microsoft Store).' }

$dotnetVersion = (dotnet --version 2>$null)
if (-not $dotnetVersion -or -not $dotnetVersion.StartsWith('10.')) {
    Install-WingetPackage 'Microsoft.DotNet.SDK.10' '.NET 10 SDK'
} else {
    Write-Ok ".NET SDK $dotnetVersion"
}

if (-not (Test-Command docker)) {
    Install-WingetPackage 'Docker.DockerDesktop' 'Docker Desktop'
    Write-Todo 'Start Docker Desktop, then re-run this script.'
    exit 0
}
Write-Ok 'Docker'

if (-not (Test-Command az)) { Install-WingetPackage 'Microsoft.AzureCLI' 'Azure CLI' } else { Write-Ok 'Azure CLI' }

# ── 2. Development certificate ──────────────────────────────────────────────────
# Azurite runs with --oauth basic, and the Azure SDK refuses to put a bearer token on an unencrypted
# connection. azurite.ps1 hands Azurite the certificate behind https://localhost:5001, so it has to be trusted
# first — otherwise the emulator starts and every storage call fails the TLS handshake.
dotnet dev-certs https --check --trust 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Trusting the ASP.NET Core development certificate (a Windows prompt will appear)...' -ForegroundColor Yellow
    dotnet dev-certs https --trust
}
Write-Ok 'Development certificate trusted'

# ── 3. Local storage and logs ───────────────────────────────────────────────────
try { docker info 2>$null | Out-Null } catch { throw 'Docker is installed but not running. Start Docker Desktop and re-run.' }
& (Join-Path $PSScriptRoot 'azurite.ps1')
Write-Ok 'Azurite on 12000 (blob) / 12002 (table), Seq on 5341'

# ── 4. Azure sign-in and secret names ───────────────────────────────────────────
$account = az account show --query user.name -o tsv 2>$null
if (-not $account) {
    Write-Host 'Not signed in to Azure — opening az login (DefaultAzureCredential reads Key Vault and Storage with it)...' -ForegroundColor Yellow
    az login | Out-Null
    $account = az account show --query user.name -o tsv
}
Write-Ok "az login as $account"

# Names only. A value printed to a console is a value in a scrollback buffer.
$required = @(
    'PoFightJudge--GeminiApiKey',
    'PoFightJudge--AzureAd--TenantId',
    'PoFightJudge--AzureAd--ClientId'
)

$names = az keyvault secret list --vault-name kv-poshared2 --query '[].name' -o tsv 2>$null
if (-not $names) {
    Write-Todo 'kv-poshared2 could not be read. The app still runs — without a Gemini key it uses deterministic fakes and says so.'
} else {
    foreach ($name in $required) {
        if ($names -contains $name) {
            Write-Ok "secret $name"
        } else {
            $key = $name.Replace('--', ':')
            Write-Todo "secret $name ($key) is missing from kv-poshared2. Set it there by hand."
        }
    }
}

# ── 5. Build and test ───────────────────────────────────────────────────────────
dotnet restore PoFightJudge.slnx
dotnet build PoFightJudge.slnx -c Release --no-restore
Write-Ok 'Build (Release, warnings are errors)'

if (-not $SkipTests) {
    # Playwright's browser is a download, not a package reference; the UI tier skips with a message without it.
    $playwright = Get-ChildItem tests/PoFightJudge.E2EUI/bin/Release -Recurse -Filter playwright.ps1 -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($playwright) { & $playwright.FullName install chromium | Out-Null; Write-Ok 'Playwright Chromium' }

    dotnet test PoFightJudge.slnx -c Release --no-build
    Write-Ok 'All four test tiers'
}

# ── 6. Run ──────────────────────────────────────────────────────────────────────
if ($Run) {
    # 5001 is the documented default, but if another Po* workspace holds it `dotnet run` binds nothing and says
    # nothing — it simply never serves. Pick the first free port instead of hanging.
    $port = 5001, 5003, 5005, 5101 | Where-Object {
        -not (Get-NetTCPConnection -LocalPort $_ -State Listen -ErrorAction SilentlyContinue)
    } | Select-Object -First 1

    if (-not $port) { $port = 5000 + (Get-Random -Maximum 1000) }

    # --no-launch-profile skips launchSettings.json, and with it ASPNETCORE_ENVIRONMENT: the app comes up as
    # Production, refuses the fakes, and logs PRODUCTION DEGRADED. The variable is what the host reads — passing
    # --environment as an argument does not reach it.
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    Write-Host "Starting https://localhost:$port (Development) ..." -ForegroundColor Cyan
    dotnet run --project src/PoFightJudge.Api -c Release --no-build --no-launch-profile --urls "https://localhost:$port"
} else {
    Write-Host ''
    Write-Host 'Ready. Start the app with:' -ForegroundColor Cyan
    Write-Host '  dotnet run --project src/PoFightJudge.Api' -ForegroundColor White
}
