#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════════
# EconGrader backup: SQL Server .bak + app_storage archive → ./backups/<ts>/
#
# Usage:  SA_PASSWORD must be in the environment (read from .env automatically).
#         KEEP_N overrides retention (default 14 backup sets).
#   ./scripts/backup.sh
#
# Output layout (referenced by scripts/restore.md):
#   backups/2026-08-26-1042/EconGrader-2026-08-26-1042.bak
#   backups/2026-08-26-1042/app-storage-2026-08-26-1042.tar.gz
# ═══════════════════════════════════════════════════════════════════════════
set -euo pipefail

cd "$(dirname "$0")/.."

# ── Load .env without echoing values ─────────────────────────────────────────
if [ -f .env ]; then
  set -a; source .env; set +a
fi

: "${SA_PASSWORD:?SA_PASSWORD is not set — add it to .env or export it}"
CONTAINER_DB="${CONTAINER_DB:-econgrader-db}"

STAMP="$(date +%Y-%m-%d-%H%M)"
DEST="backups/$STAMP"
mkdir -p "$DEST"

echo "→ Backing up database to $DEST/EconGrader-$STAMP.bak"
# The mssql image ships without this dir; BACKUP DATABASE fails unless it exists.
MSYS_NO_PATHCONV=1 docker exec "$CONTAINER_DB" mkdir -p /var/opt/mssql/backup
MSYS_NO_PATHCONV=1 docker exec "$CONTAINER_DB" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa \
  -P "$SA_PASSWORD" -Q "
    BACKUP DATABASE [EconGrader]
    TO DISK = '/var/opt/mssql/backup/EconGrader-$STAMP.bak'
    WITH INIT, COMPRESSION, CHECKSUM;" > /dev/null
# Copy out of the container and remove the in-container copy.
MSYS_NO_PATHCONV=1 docker cp "$CONTAINER_DB":/var/opt/mssql/backup/EconGrader-"$STAMP".bak "$DEST/"
MSYS_NO_PATHCONV=1 docker exec "$CONTAINER_DB" rm -f "/var/opt/mssql/backup/EconGrader-$STAMP.bak"

echo "→ Archiving answer images volume to $DEST/app-storage-$STAMP.tar.gz"
# Volume name = <project-dir>_app_storage (compose naming). Fall back to the
# api container's own mount if the project was renamed.
STORAGE_VOL="${STORAGE_VOL:-$(docker volume ls --format '{{.Name}}' | grep '_app_storage$' | head -1)}"
: "${STORAGE_VOL:?No *_app_storage volume found — is the stack running/composed?}"
MSYS_NO_PATHCONV=1 docker run --rm -v "$STORAGE_VOL":/storage:ro -v "$(pwd)/$DEST:/backup" alpine \
  tar czf "/backup/app-storage-$STAMP.tar.gz" -C /storage .

echo "→ Backup complete:"
ls -lh "$DEST" | awk 'NR>1 {printf "   %-45s %s\n", $NF, $5}'

# ── Retention: keep last N stamp directories ────────────────────────────────
KEEP_N="${KEEP_N:-14}"
mapfile -t old < <(ls -1d backups/*/ 2>/dev/null | sort | head -n -"$KEEP_N")
if [ "${#old[@]}" -gt 0 ]; then
  echo "→ Pruning ${#old[@]} old backup set(s) (keeping $KEEP_N)"
  rm -rf "${old[@]}"
fi
