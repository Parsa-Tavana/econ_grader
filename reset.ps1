# ============================================================
#  EconGrader - FULL RESET (destroys DB + containers + volumes)
# ============================================================
# Use ONLY when: first run, or the database is broken/empty and
# you want a clean slate.
#
#   powershell -ExecutionPolicy Bypass -File "c:\Users\SHPA-N6\Desktop\econ_grader\reset.ps1"
# Or double-click reset.bat
#
# WARNING: deletes ALL data (exams, answers, grading history).

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
        Write-Host "Docker engine is not running. Please start Docker Desktop manually, then re-run this script." -ForegroundColor Yellow
        throw "Docker engine unreachable."
    }

    Write-Host "Starting Docker Desktop..." -ForegroundColor Cyan
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

Write-Host "`nThis will DELETE all EconGrader data (database volume) and rebuild." -ForegroundColor Yellow
$confirm = Read-Host "Type YES to continue"
if ($confirm -ne "YES") { Write-Host "Aborted."; exit }

Write-Host "`n=== [1/4] Stopping everything and removing volumes ===" -ForegroundColor Cyan
Push-Location $root
try {
    docker compose down -v

    Write-Host "`n=== [2/4] Rebuilding images (clean, may take a few minutes) ===" -ForegroundColor Cyan
    docker compose build
    if ($LASTEXITCODE -ne 0) { throw "docker compose build failed - see output above." }

    Write-Host "`n=== [3/4] Starting services ===" -ForegroundColor Cyan
    docker compose up -d

    Write-Host "`n=== [4/4] Waiting for API + checking migration ===" -ForegroundColor Cyan
    $deadline = (Get-Date).AddMinutes(8)
    $migrated = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        try {
            $logs = docker logs econgrader-api 2>&1 | Out-String
            if ($logs -match "Database migrated successfully") { $migrated = $true; break }
            if ($logs -match "Database migration failed") {
                Write-Host "MIGRATION FAILED - run 'docker logs econgrader-api --tail 100' for diagnosis" -ForegroundColor Red
                break
            }
        } catch { }
        Write-Host "   waiting for api container..."
    }
} finally {
    Pop-Location
}

if ($migrated) {
    Write-Host "`nDONE - database schema created successfully." -ForegroundColor Green
    Write-Host "Now just double-click start.bat (or run start.ps1)." -ForegroundColor Green
} else {
    Write-Host "`nMigration state unknown - check logs with: docker logs econgrader-api" -ForegroundColor Yellow
}
