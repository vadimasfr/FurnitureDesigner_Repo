<#
.SYNOPSIS
    Builds, tests and starts Furniture Designer. Run it again after every code change.

.EXAMPLE
    .\run.ps1              # web app at http://localhost:5080 (opens the browser)
    .\run.ps1 -Docker      # web app in Docker at http://localhost:8080
    .\run.ps1 -Desktop     # Windows desktop app (WPF)
    .\run.ps1 -SkipTests   # skip the unit tests for a faster start
#>
param(
    [switch]$Docker,
    [switch]$Desktop,
    [switch]$SkipTests,
    [switch]$NoBrowser,
    [int]$Port = 5080
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

function Invoke-Checked($description) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "`n$description failed (exit code $LASTEXITCODE)." -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

# Opens the browser once the app answers on /health, without blocking the app's console output.
function Open-BrowserWhenReady($url, $timeoutSeconds) {
    if ($NoBrowser) { return }
    Start-Job -ArgumentList $url, $timeoutSeconds -ScriptBlock {
        param($url, $timeoutSeconds)
        for ($i = 0; $i -lt $timeoutSeconds; $i++) {
            try {
                Invoke-WebRequest "$url/health" -UseBasicParsing -TimeoutSec 2 | Out-Null
                Start-Process "$url/?sample"
                return
            } catch {
                Start-Sleep -Seconds 1
            }
        }
    } | Out-Null
}

# A terminal opened before the key was saved does not see it yet: take it from the user's environment.
if (-not $env:ANTHROPIC_API_KEY) {
    $env:ANTHROPIC_API_KEY = [Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY', 'User')
}
if (-not $env:ANTHROPIC_API_KEY -and -not $Docker) {
    Write-Host 'Note: ANTHROPIC_API_KEY is not set, so the AI tab will not work (everything else does).' -ForegroundColor Yellow
}

Step 'Stopping a previous run'
# A running app locks its files and the port, so the build and start would fail.
Get-Process -Name 'FurnitureDesigner', 'FurnitureDesigner.Web' -ErrorAction SilentlyContinue | Stop-Process -Force
# Also whatever still holds the port, e.g. a copy started with plain `dotnet FurnitureDesigner.Web.dll`.
if (-not $Docker -and -not $Desktop) {
    Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
}
if ($Docker) {
    docker compose down 2>$null | Out-Null
}

Step 'Building'
dotnet build FurnitureDesigner.slnx -nologo -v q
Invoke-Checked 'Build'

if (-not $SkipTests) {
    Step 'Running tests'
    dotnet test tests/FurnitureDesigner.Core.Tests --no-build --nologo -v q
    Invoke-Checked 'Tests'
}

if ($Desktop) {
    Step 'Starting the desktop app'
    dotnet run --project src/FurnitureDesigner.App --no-build
    exit $LASTEXITCODE
}

if ($Docker) {
    if (-not (Test-Path .env)) {
        Write-Host 'Note: no .env file - copy .env.example to .env and add your ANTHROPIC_API_KEY for the AI tab.' -ForegroundColor Yellow
    }
    $url = 'http://localhost:8080'
    Step "Starting in Docker at $url (Ctrl+C stops it)"
    Open-BrowserWhenReady $url 600  # the first image build takes a few minutes
    docker compose up --build
    exit $LASTEXITCODE
}

$url = "http://localhost:$Port"
Step "Starting the web app at $url (Ctrl+C stops it)"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
Open-BrowserWhenReady $url 60
dotnet run --project src/FurnitureDesigner.Web --no-build --no-launch-profile --urls $url
