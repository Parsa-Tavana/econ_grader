# ============================================================
#  EconGrader — DAILY START (fast, reuses existing containers)
# ============================================================
# Run from anywhere:
#   powershell -ExecutionPolicy Bypass -File "c:\Users\SHPA-N6\Desktop\econ_grader\start.ps1"
#
# What it does:
#   1. Starts db + grading + api (builds only if something changed)
#   2. Waits until all are healthy
#   3. Starts the frontend dev server (opens http://localhost:5173)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "`n=== [1/4] Starting backend services (Docker) ===" -ForegroundColor Cyan
Push-Location $root
docker compose up -d --build
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "docker compose up failed" }

Write-Host "`n=== [2/4] Waiting for health checks ===" -ForegroundColor Cyan
$deadline = (Get-Date).AddMinutes(3)
do {
    Start-Sleep -Seconds 3
    $status = docker compose ps --format json | ConvertFrom-Json
    $unhealthy = @($status | Where-Object { $_.Health -eq "starting" }).Count
    Write-Host ("   waiting... unhealthy/starting: {0}" -f $unhealthy)
} while ($unhealthy -gt 0 -and (Get-Date) -lt $deadline)

Write-Host "`n=== [3/4] Verifying API ===" -ForegroundColor Cyan
try {
    $h = Invoke-RestMethod -Uri "http://localhost:8080/api/health" -TimeoutSec 10
    Write-Host ("   API OK — grading service up: {0}" -f $h.dependencies.gradingService.up) -ForegroundColor Green
    if (-not $h.dependencies.gradingService.up) {
        Write-Host "   WARNING: Python grading service is DOWN. AI grading will fail." -ForegroundColor Yellow
        Write-Host "   Check: docker logs econgrader-grading --tail 50" -ForegroundColor Yellow
    }
} catch {
    Write-Host "   API not reachable yet — check: docker logs econgrader-api --tail 50" -ForegroundColor Red
}

Pop-Location

Write-Host "`n=== [4/4] Starting frontend ===" -ForegroundColor Cyan
Write-Host "   URL after startup: http://localhost:5173" -ForegroundColor Green
Set-Location (Join-Path $root "frontend")
npm run dev