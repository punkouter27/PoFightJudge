<#
.SYNOPSIS
    Checks the live site after the shared deploy.yml health gate has passed.

.DESCRIPTION
    The WASM shell, runtime and manifest are served, the API is protected, and no catch-all
    swallows API routes. A cold F1 instance answers health before the rest of the app is warm,
    so every assertion gets a few attempts of its own.

.EXAMPLE
    pwsh ./scripts/post-deploy-smoke.ps1 -BaseUrl https://<app>.azurewebsites.net
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BaseUrl)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$BaseUrl = $BaseUrl.TrimEnd('/')

function Get-Status([string]$Path) {
    try {
        $r = Invoke-WebRequest -Uri "$BaseUrl$Path" -Method Get -TimeoutSec 20 -SkipHttpErrorCheck -MaximumRedirection 0 -ErrorAction Stop
        return [int]$r.StatusCode
    }
    catch { return 0 }
}

function Assert-Status([string]$Path, [int]$Expected, [int]$Attempts = 6, [int]$DelaySeconds = 5) {
    $got = 0
    for ($i = 1; $i -le $Attempts; $i++) {
        $got = Get-Status $Path
        if ($got -eq $Expected) { Write-Host "$Path -> $got"; return }
        Start-Sleep -Seconds $DelaySeconds
    }
    Write-Host "::error::$Path returned $got, expected $Expected"
    exit 1
}

Assert-Status '/api/health' 200 -Attempts 30 -DelaySeconds 10
Assert-Status '/login' 200                  # WASM shell served
Assert-Status '/_framework/dotnet.js' 200   # WASM runtime present (no blazor.boot.json in .NET 10)
Assert-Status '/manifest.webmanifest' 200   # installable
Assert-Status '/api/matches' 401            # the API is protected
Assert-Status '/api/no-such-route' 404      # no catch-all swallowing API routes
Write-Host 'Smoke test passed.'
