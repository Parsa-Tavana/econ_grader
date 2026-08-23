# ============================================================
#  EconGrader — FULL RESET (destroys DB + containers + images)
# ============================================================
# Use ONLY when: first run after the cascade-FK fix, or the database
# is broken/empty and you want a clean slate.
#
#   powershell -ExecutionPolicy Bypass -File "c:\Users\SHPA-N6\Desktop\econ_grader\reset.ps1"
#
# WARNING: -v deletes ALL data (exams, answers, grading history).

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "`nThis will DELETE all EconGrader data (database volume) and rebuild." -ForegroundColor Yellow
$confirm = Read-Host "Type YES to continue"
if ($confirm -ne "YES") { Write-Host "Aborted."; exit }

Write-Host "`n=== [1/4] Stopping everything and removing volumes ===" -ForegroundColor Cyan
Push-Location $root
docker compose down -v

Write-Host "`n=== [2/4] Rebuilding images (clean, may take a few minutes) ===" -ForegroundColor Cyan
docker compose build

Write-Host "`n=== [3/4] Starting services ===" -ForegroundColor Cyan
docker compose up -d

Write-Host "`n=== [4/4] Waiting for API + checking migration ===" -ForegroundColor Cyan
$deadline = (Get-Date).AddMinutes(4)
$migrated = $false
do {
    Start-Sleep -Seconds 5
    try {
        $logs = docker logs econgrader-api 2>&1 | Out-String
        if ($logs -match "Database migrated successfully") { $migrated = $true; break }
        if ($logs -match "Database migration failed") {
            Write-Host "MIGRATION FAILED — paste 'docker logs econgrader-api --tail 100' for diagnosis" -ForegroundColor Red
            break
        }
    } catch { }
    Write-Host "   waiting for api container..."
} while ((Get-Date) -lt $deadline)

Pop-Location

if ($migrated) {
    Write-Host "`nDONE — database schema created successfully." -ForegroundColor Green
    Write-Host "Now start the frontend:" -ForegroundColor Green
    Write-Host "  cd frontend ; npm run dev      → http://localhost:5173"
    Write-Host "`nOr just run start.ps1 next time."
} else {
    Write-Host "`nMigration state unknown — check logs with: docker logs econgrader-api" -ForegroundColor Yellow
}