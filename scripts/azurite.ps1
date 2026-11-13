<#
.SYNOPSIS
    Starts the local Azurite storage emulator with OAuth + HTTPS enabled.

.DESCRIPTION
    Azurite has to speak HTTPS for token auth to work at all — the Azure SDK will
    not put a bearer token on an unencrypted connection. Rather than minting a new
    self-signed CA and asking you to trust it, this exports the ASP.NET Core dev
    certificate you already trust (the one behind https://localhost:5001) and hands
    that to Azurite. The app then validates Azurite's TLS with no extra trust step.

    Result: DefaultAzureCredential authenticates to the local emulator exactly as it
    does to real Azure Storage, and the solution keeps its no-connection-string rule.

.PARAMETER Recreate
    Force a fresh certificate export even if one already exists.

.PARAMETER Down
    Stop the emulator instead of starting it.

.PARAMETER Wipe
    With -Down, also delete the data volume (throws away all local tables/blobs).

.EXAMPLE
    .\SCRIPTS\azurite.ps1
    .\SCRIPTS\azurite.ps1 -Down -Wipe
#>
[CmdletBinding()]
param(
    [switch]$Recreate,
    [switch]$Down,
    [switch]$Wipe
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$certDir = Join-Path $repoRoot 'certs'
$certPath = Join-Path $certDir 'azurite.pfx'

# Matches the compose default. Local dev cert for a loopback emulator — there is
# nothing here worth protecting, and a fixed value keeps the compose file runnable
# without an .env.
$certPassword = if ($env:AZURITE_CERT_PASSWORD) { $env:AZURITE_CERT_PASSWORD } else { 'azurite' }

Push-Location $repoRoot
try {
    if ($Down) {
        Write-Host 'Stopping Azurite...' -ForegroundColor Cyan
        if ($Wipe) {
            docker compose down -v
            Write-Host 'Stopped. Data volume deleted.' -ForegroundColor Yellow
        }
        else {
            docker compose down
            Write-Host 'Stopped. Data volume kept (-Wipe to delete it).' -ForegroundColor Green
        }
        return
    }

    # ── Docker ───────────────────────────────────────────────────────────────
    docker info 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Docker is not running. Start Docker Desktop and re-run.' -ForegroundColor Red
        exit 1
    }

    # ── Certificate ──────────────────────────────────────────────────────────
    if ($Recreate -and (Test-Path $certPath)) { Remove-Item $certPath -Force }

    if (-not (Test-Path $certPath)) {
        New-Item -ItemType Directory -Force -Path $certDir | Out-Null
        Write-Host 'Exporting the ASP.NET Core dev certificate for Azurite...' -ForegroundColor Cyan
        dotnet dev-certs https --export-path $certPath --password $certPassword --format Pfx --quiet
        if ($LASTEXITCODE -ne 0) {
            Write-Host 'Export failed. Run "dotnet dev-certs https --trust" once, then re-run this script.' -ForegroundColor Red
            exit 1
        }
        Write-Host "  Wrote $certPath" -ForegroundColor Green
    }
    else {
        Write-Host "Certificate already present ($certPath). Use -Recreate to refresh it." -ForegroundColor DarkGray
    }

    # The app trusts Azurite only because this cert is in the machine trust store.
    # If it never got trusted, every storage call fails a TLS handshake with an
    # error that does not mention certificates at all — so say so up front.
    $trusted = dotnet dev-certs https --check --trust 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'The ASP.NET Core dev cert is not trusted on this machine.' -ForegroundColor Yellow
        Write-Host '  Run: dotnet dev-certs https --trust' -ForegroundColor Yellow
        Write-Host '  Without it, storage calls fail with an opaque TLS error.' -ForegroundColor Yellow
    }

    # ── Up ───────────────────────────────────────────────────────────────────
    Write-Host 'Starting Azurite (OAuth + HTTPS)...' -ForegroundColor Cyan
    $env:AZURITE_CERT_PASSWORD = $certPassword
    docker compose up -d
    if ($LASTEXITCODE -ne 0) { Write-Host 'docker compose up failed.' -ForegroundColor Red; exit 1 }

    Write-Host ''
    Write-Host 'Azurite is up.' -ForegroundColor Green
    Write-Host '  Blob   https://127.0.0.1:12000/devstoreaccount1' -ForegroundColor Gray
    Write-Host '  Table  https://127.0.0.1:12002/devstoreaccount1' -ForegroundColor Gray
    Write-Host ''
    Write-Host 'The API uses it whenever Features:UseAzurite is true (default in Development).' -ForegroundColor Gray
    Write-Host 'Set it to false in appsettings.Development.json to go back to real dev storage.' -ForegroundColor Gray
}
finally {
    Pop-Location
}
