# ============================================================
#  EconGrader - SHARE DEMO (public URL via Cloudflare tunnel)
# ============================================================
# Run from anywhere:
#   powershell -ExecutionPolicy Bypass -File "c:\Users\SHPA-N6\Desktop\econ_grader\share-demo.ps1"
# Or simply double-click share-demo.bat
#
# What it does:
#   1. Starts the backend exactly like start.ps1 (Docker db + grading + api)
#   2. Starts the Vite dev server (npm run dev) if not already running
#   3. Downloads cloudflared.exe if missing (one time)
#   4. Opens a free "quick tunnel" to the frontend and prints the public URL
#
# The URL looks like https://xxxx-yyyy-zzzz.trycloudflare.com and works from
# ANY computer with internet. NOTE:
#   - A NEW random URL is generated every run (that's normal for free tunnels).
#   - Everyone who opens it shares YOUR database and YOUR AI credits.
#   - Keep this window open; closing it (Ctrl+C) kills the demo link.

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Test-DockerEngine {
    cmd /c "docker info >nul 2>&1"
    return ($LASTEXITCODE -eq 0)
}

# --- 1. Backend (same as start.ps1) ---------------------------------------
if (-not (Test-DockerEngine)) {
    Write-Host "Starting Docker Desktop..." -ForegroundColor Cyan
    Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe" -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddMinutes(4)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        if (Test-DockerEngine) { break }
        Write-Host "   waiting for Docker engine..."
    }
    if (-not (Test-DockerEngine)) { throw "Docker engine did not come online." }
}

Write-Host "`n=== [1/4] Backend containers (db + grading + api) ===" -ForegroundColor Cyan
Push-Location $root
try {
    docker compose up -d --build | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed - see output above." }
} finally {
    Pop-Location
}

$deadline = (Get-Date).AddMinutes(10)
$apiOk = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    try {
        $health = Invoke-RestMethod -Uri "http://localhost:8080/api/health" -TimeoutSec 5
        $apiOk = $true
        break
    } catch {
        Write-Host "   waiting for API..."
    }
}
if (-not $apiOk) { throw "API did not answer within 10 minutes. Check 'docker compose ps'." }
Write-Host ("   API OK - status={0}" -f $health.status) -ForegroundColor Green

# --- 2. Frontend dev server -------------------------------------------------
Write-Host "`n=== [2/4] Frontend dev server (:5173) ===" -ForegroundColor Cyan
Set-Location (Join-Path $root "frontend")
if (-not (Test-Path ".\node_modules")) {
    Write-Host "   node_modules missing - running npm install (one time)..." -ForegroundColor Cyan
    npm install
    if ($LASTEXITCODE -ne 0) { throw "npm install failed - see output above." }
}

$viteRunning = Get-NetTCPConnection -LocalPort 5173 -State Listen -ErrorAction SilentlyContinue
if ($viteRunning) {
    Write-Host "   Vite already running on :5173 - reusing it." -ForegroundColor Green
    Write-Host "   (If you edited vite.config.ts recently, restart it so host/allowedHosts apply.)" -ForegroundColor Yellow
} else {
    Write-Host "   Starting vite in a separate window..." -ForegroundColor Cyan
    Start-Process -FilePath "cmd.exe" `
        -ArgumentList "/c", "npm", "run", "dev" `
        -WorkingDirectory (Join-Path $root "frontend") `
        -WindowStyle Normal
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        $viteRunning = Get-NetTCPConnection -LocalPort 5173 -State Listen -ErrorAction SilentlyContinue
    } until ($viteRunning -or (Get-Date) -gt $deadline)
    if (-not $viteRunning) { throw "Vite did not come up on :5173 within 60s." }
    Write-Host "   Vite UP." -ForegroundColor Green
}

# --- 3. cloudflared ----------------------------------------------------------
Write-Host "`n=== [3/4] Tunnel (cloudflared) ===" -ForegroundColor Cyan
$toolsDir = Join-Path $env:LOCALAPPDATA "EconGraderTools"
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
$cloudflared = Join-Path $toolsDir "cloudflared.exe"

if (-not (Test-Path $cloudflared)) {
    Write-Host "   Downloading cloudflared (one time)..." -ForegroundColor Cyan
    $url = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe"
    Invoke-WebRequest -Uri $url -OutFile $cloudflared -UseBasicParsing
    Unblock-File $cloudflared
}
Write-Host "   cloudflared ready: $cloudflared" -ForegroundColor Green

# --- 4. Public URL -----------------------------------------------------------
Write-Host "`n=== [4/4] Opening public tunnel... ===" -ForegroundColor Cyan
$log = Join-Path $toolsDir ("tunnel-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")
$tunnelProc = Start-Process -FilePath $cloudflared `
    -ArgumentList @("tunnel", "--url", "http://localhost:5173", "--no-autoupdate") `
    -RedirectStandardOutput $log -RedirectStandardError ($log + ".err") `
    -PassThru -WindowStyle Hidden

$url = $null
$deadline = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    # cloudflared logs the URL to stderr
    $txt = ""
    if (Test-Path ($log + ".err")) { $txt += Get-Content ($log + ".err") -Raw -ErrorAction SilentlyContinue }
    if (Test-Path $log) { $txt += Get-Content $log -Raw -ErrorAction SilentlyContinue }
    if ($txt -match "https://[a-z0-9\-]+\.trycloudflare\.com") {
        $url = $Matches[0]
        break
    }
    if ($tunnelProc.HasExited) { throw "cloudflared exited early - check $log.err" }
}
if (-not $url) { throw "Could not find the public URL in cloudflared logs. Check $log.err" }

Write-Host ""
Write-Host "=================================================================" -ForegroundColor Green
Write-Host "  PUBLIC DEMO URL:" -ForegroundColor Green
Write-Host "     $url" -ForegroundColor Yellow
Write-Host "" -ForegroundColor Yellow
Write-Host "  Share this on the other desktops. Works over https, no install." -ForegroundColor Green
Write-Host "  New random URL every run. Closing this window stops the demo." -ForegroundColor DarkGray
Write-Host "=================================================================" -ForegroundColor Green

try {
    Set-Clipboard -Value $url
    Write-Host "(URL copied to clipboard)" -ForegroundColor DarkGray
} catch {}

# Optional: open it here too, so you can confirm it works end-to-end.
Start-Process $url

# Keep the console alive while the tunnel runs; Ctrl+C (or closing the window)
# kills cloudflared and stops the demo link.
try {
    Wait-Process -Id $tunnelProc.Id
} finally {
    if (-not $tunnelProc.HasExited) { Stop-Process -Id $tunnelProc.Id -Force -ErrorAction SilentlyContinue }
}
Write-Host "`nTunnel closed. Demo stopped." -ForegroundColor Yellow
