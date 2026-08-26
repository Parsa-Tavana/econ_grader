# ═══════════════════════════════════════════════════════════════════════════
# EconGrader backup: SQL Server .bak + app_storage archive → .\backups\<ts>\
#
# Usage (from repo root):  .\scripts\backup.ps1 [-KeepN 14]
# SA_PASSWORD is read from .env automatically. Never echoes secrets.
#
# Output layout (referenced by scripts/restore.md):
#   backups\2026-08-26-1042\EconGrader-2026-08-26-1042.bak
#   backups\2026-08-26-1042\app-storage-2026-08-26-1042.tar.gz
# ═══════════════════════════════════════════════════════════════════════════
[CmdletBinding()]
param(
    # Retention: how many backup SETS to keep.
    [int]$KeepN = 14
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

# ── Load .env without echoing values ─────────────────────────────────────────
if (-not (Test-Path ".env")) { throw ".env not found in repo root — copy .env.example and fill it in." }
Get-Content ".env" | ForEach-Object {
    if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$') {
        [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], "Process") | Out-Null
    }
}
if ([string]::IsNullOrWhiteSpace($env:SA_PASSWORD)) {
    throw "SA_PASSWORD is not set — add it to .env or export it first."
}

$Stamp = Get-Date -Format "yyyy-MM-dd-HHmm"
$Dest = Join-Path "backups" $Stamp
New-Item -ItemType Directory -Force -Path $Dest | Out-Null

Write-Host "-> Backing up database to $dest/EconGrader-$Stamp.bak"
# The mssql image ships without this dir; BACKUP DATABASE fails unless it exists.
docker exec econgrader-db mkdir -p /var/opt/mssql/backup
if ($LASTEXITCODE -ne 0) { throw "could not create backup dir in db container" }
docker exec econgrader-db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa `
  -P $env:SA_PASSWORD -Q @"
BACKUP DATABASE [EconGrader]
TO DISK = '/var/opt/mssql/backup/EconGrader-$Stamp.bak'
WITH INIT, COMPRESSION, CHECKSUM;
"@ | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sqlcmd BACKUP DATABASE failed" }
docker cp "econgrader-db:/var/opt/mssql/backup/EconGrader-$Stamp.bak" "$Dest/"
if ($LASTEXITCODE -ne 0) { throw "docker cp of backup file failed" }
docker exec econgrader-db rm -f "/var/opt/mssql/backup/EconGrader-$Stamp.bak"

Write-Host "-> Archiving answer images volume to backups/$Stamp/app-storage-$Stamp.tar.gz"
$vol = (docker volume ls --format "{{.Name}}" | Select-String "_app_storage$" | Select-Object -First 1).ToString()
if (-not $vol) { throw "No *_app_storage volume found — is the stack running/composed?" }
docker run --rm -v "${vol}:/storage:ro" -v "$(Resolve-Path $Dest):/backup" alpine `
  tar czf "/backup/app-storage-$Stamp.tar.gz" -C /storage .
if ($LASTEXITCODE -ne 0) { throw "storage archive failed" }

Write-Host "-> Backup complete:"
Get-ChildItem $Dest | ForEach-Object {
    Write-Host ("   {0,-45} {1:N0} bytes" -f $_.Name, $_.Length)
}

# ── Retention: keep last N stamp directories ────────────────────────────────
$sets = Get-ChildItem backups -Directory | Sort-Object Name
if ($sets.Count -gt $KeepN) {
    $old = $sets | Select-Object -First ($sets.Count - $KeepN)
    Write-Host "-> Pruning $($old.Count) old backup set(s) (keeping $KeepN)"
    $old | Remove-Item -Recurse -Force
}
