# Restore procedure

> An untested restore path is not a backup. Rehearse this on a spare machine
> before you ever need it.

Backup sets live in `backups/<timestamp>/` and always contain exactly two
files (produced by `scripts/backup.sh` / `scripts/backup.ps1`):

```
EconGrader-<timestamp>.bak        # SQL Server backup (COMPRESSION + CHECKSUM)
app-storage-<timestamp>.tar.gz    # contents of the app_storage docker volume
```

`<timestamp>` looks like `2026-08-26-1042`. Replace it below with the set you
are restoring. All commands run from the repo root.

---

## 0. Prerequisites

- A working `.env` (copy `.env.example`, fill in). **The `SA_PASSWORD` /
  `DB_APP_PASSWORD` in the restored environment should be the ones from when
  the backup was taken** if you plan to keep old app users; otherwise create
  the app login again with step 5.
- Docker compose stack stopped or running-fresh; the steps below assume the
  stack from `docker compose up -d db` only.

## 1. Start an empty SQL Server container

```bash
docker compose up -d db
# wait for healthy:
docker inspect --format "{{.State.Health.Status}}" econgrader-db   # → healthy
```

## 2. Copy the .bak into the container and restore

```bash
MSYS_NO_PATHCONV=1 docker exec econgrader-db mkdir -p /var/opt/mssql/backup

MSYS_NO_PATHCONV=1 docker cp backups/<timestamp>/EconGrader-<timestamp>.bak \
  econgrader-db:/var/opt/mssql/backup/EconGrader.bak

MSYS_NO_PATHCONV=1 docker exec econgrader-db /opt/mssql-tools18/bin/sqlcmd -C -S localhost \
  -U sa -P "$SA_PASSWORD" -Q `
  "RESTORE DATABASE [EconGrader] FROM DISK = '/var/opt/mssql/backup/EconGrader.bak' WITH MOVE 'EconGrader' TO '/var/opt/mssql/data/EconGrader.mdf', MOVE 'EconGrader_log' TO '/var/opt/mssql/data/EconGrader_log.ldf', REPLACE;"
```

## 3. VERIFY the database restore (do not skip)

```bash
MSYS_NO_PATHCONV=1 docker exec econgrader-db /opt/mssql-tools18/bin/sqlcmd -C -S localhost \
  -U sa -P "$SA_PASSWORD" -d EconGrader -Q "
    SET NOCOUNT ON;
    SELECT COUNT(*) AS exams FROM Exams;
    SELECT TOP 5 Id, Name, CreatedAt FROM Exams ORDER BY CreatedAt DESC;
    SELECT COUNT(*) AS answers FROM Answers;"
```

Expect plausible counts matching what you had (compare against a known exam
name). A restore that returns 0 rows everywhere restored the wrong .bak or an
empty database — stop and investigate before continuing.

## 4. Restore the answer-images volume

```bash
# find the actual volume name (usually econgrader_app_storage):
docker volume ls --format "{{.Name}}" | grep _app_storage

docker run --rm -v econgrader_app_storage:/storage -v "$(pwd)/backups/<timestamp>:/backup:ro" alpine \
  sh -c "rm -rf /storage/* && tar xzf /backup/app-storage-<timestamp>.tar.gz -C /storage"
```

## 5. Recreate the least-privilege app login

The login lives in `master` (server scope) and is NOT inside the database
backup — recreate it. sqlcmd cannot read a script from stdin, so mount the
file into the container first:

```bash
MSYS_NO_PATHCONV=1 docker cp scripts/init-db.sql econgrader-db:/tmp/init-db.sql

MSYS_NO_PATHCONV=1 docker exec econgrader-db /opt/mssql-tools18/bin/sqlcmd \
  -C -S localhost -U sa -P "$SA_PASSWORD" -d master \
  -v AppPassword="$DB_APP_PASSWORD" -i /tmp/init-db.sql

MSYS_NO_PATHCONV=1 docker exec econgrader-db rm -f /tmp/init-db.sql
```

Expected output ends with: `Done — econgrader_app is db_owner of EconGrader only.`

## 6. Bring up the rest of the stack and verify end-to-end

```bash
docker compose up -d --build
curl -s http://localhost/api/health          # {"status":"ok",...gradingService.up:true}
curl -s -o /dev/null -w "%{http_code}\n" http://localhost/   # 200, SPA served
```

Then sign in through the UI and open one answer image — if the scan renders,
the volume restore is good (absolute paths stored in the DB match the
restored files).

## 7. Clean up the staging copy inside the container (optional)

```bash
MSYS_NO_PATHCONV=1 docker exec econgrader-db rm -f /var/opt/mssql/backup/EconGrader.bak
```
