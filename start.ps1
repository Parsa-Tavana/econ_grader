# ============================================================
#  EconGrader - DAILY START (one click / one command)
# ============================================================
# Run from anywhere:
#   powershell -ExecutionPolicy Bypass -File "c:\Users\SHPA-N6\Desktop\econ_grader\start.ps1"
# Or simply double-click start.bat
#
# What it does:
#   1. Makes sure Docker Desktop + engine are running (starts them if not)
#   2. Starts db + grading + api (rebuilds only what changed)
#   3. Waits until the API answers on http://localhost:8080/api/health
#      (this proves db is healthy AND the api container is up)
#   4. Installs frontend deps if needed, then runs npm run dev
#
# Safe to run repeatedly - it skips whatever is already done.

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Test-DockerEngine {
    cmd /c "docker info >nul 2>&1"
    return ($LASTEXITCODE -eq 0)
}

function Ensure-Docker {
    if (Test-DockerEngine) {
        Write-Host "Docker engine already running." -ForegroundColor Green
        return
    }

    $dockerCli = Get-Command docker -ErrorAction SilentlyContinue
    $desktopExe = "C:\Program Files\Docker\Docker\Docker Desktop.exe"

    if (-not $dockerCli -and -not (Test-Path $desktopExe)) {
        throw "Docker does not appear to be installed (no docker CLI, no Docker Desktop). Install Docker Desktop first."
    }

    if ($dockerCli -and -not (Test-Path $desktopExe)) {
        # Custom install location - can't auto-start, tell the user.
        Write-Host "Docker engine is not running. Please start Docker Desktop manually, then re-run this script." -ForegroundColor Yellow
        throw "Docker engine unreachable."
    }

    Write-Host "Starting Docker Desktop (first start after boot can take a minute)..." -ForegroundColor Cyan
    Start-Process $desktopExe

    $deadline = (Get-Date).AddMinutes(4)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        if (Test-DockerEngine) {
            Write-Host "Docker engine ready." -ForegroundColor Green
            return
        }
        Write-Host "   waiting for Docker engine..."
    }
    throw "Docker engine did not come online within 4 minutes. Start Docker Desktop manually and retry."
}

Ensure-Docker

Write-Host "`n=== [1/3] Starting backend services (Docker) ===" -ForegroundColor Cyan
Push-Location $root
try {
    docker compose up -d --build
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed - see output above." }
} finally {
    Pop-Location
}

Write-Host "`n=== [2/3] Waiting for the API (db -> grading -> api chain) ===" -ForegroundColor Cyan
Write-Host "   First ever run downloads base images and starts SQL Server; this can take several minutes." -ForegroundColor DarkGray
$deadline = (Get-Date).AddMinutes(10)
$health = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    try {
        $health = Invoke-RestMethod -Uri "http://localhost:8080/api/health" -TimeoutSec 5
        break
    } catch {
        Write-Host "   waiting for API..."
    }
}

if ($null -eq $health) {
    Write-Host "API did not answer within 10 minutes. Diagnose with:" -ForegroundColor Red
    Write-Host "   docker compose ps" -ForegroundColor Red
    Write-Host "   docker logs econgrader-api --tail 50" -ForegroundColor Red
    exit 1
}

Write-Host ("   API OK - status={0}" -f $health.status) -ForegroundColor Green
if ($health.dependencies.gradingService.up) {
    Write-Host "   Python grading service: UP" -ForegroundColor Green
} else {
    Write-Host "   WARNING: Python grading service is DOWN. AI grading will fail (everything else works)." -ForegroundColor Yellow
    Write-Host "   Check: docker logs econgrader-grading --tail 50" -ForegroundColor Yellow
}

Write-Host "`n=== [3/3] Starting frontend ===" -ForegroundColor Cyan
Set-Location (Join-Path $root "frontend")
if (-not (Test-Path ".\node_modules")) {
    Write-Host "   node_modules missing - running npm install (one time)..." -ForegroundColor Cyan
    npm install
    if ($LASTEXITCODE -ne 0) { throw "npm install failed - see output above." }
}

Write-Host "   URL: http://localhost:5173" -ForegroundColor Green
Write-Host "   (leave this window open while developing; Ctrl+C stops the dev server)" -ForegroundColor DarkGray
npm run dev
