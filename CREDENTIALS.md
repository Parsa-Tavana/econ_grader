# 🔑 LOCAL CREDENTIALS — EconGrader

> ⚠️ **This file contains REAL passwords.** It is git-ignored — never commit,
> share, or screen-share it. Regenerate anytime: it mirrors `.env`.
> Generated 2026-08-26 from `.env`.

---

## Quick reference

| Service | Address | Login | Password |
|---|---|---|---|
| SQL Server (SSMS/Azure Data Studio) | `127.0.0.1,1433` *(comma!)* | `sa` | `PHkvHfPPXgAUOBCygmQRYU8S!Aa1` |
| SQL Server (app's own login — don't use manually) | `db,1433` inside compose network | `econgrader_app` | `sf4UmegX6zdEWu8kuIzqeSza!Bb2` |
| Web app | http://localhost | your admin account | *(you created it)* |
| API health | http://localhost:8080/api/health | — | — |
| Dozzle (logs) | http://localhost:9999 | none | none |
| Filebrowser (storage) | http://localhost:9998 | `admin` | `admin` ← **CHANGE ON FIRST LOGIN** |
| Langfuse (AI calls) | http://localhost:3000 | *(your account)* | *(you created it)* |

---

## 1. Database — SSMS / Azure Data Studio

**Connection dialog:**

| Field | Value |
|---|---|
| Server name | `127.0.0.1,1433` ← comma, NOT colon |
| Authentication | SQL Server Authentication |
| Login | `sa` |
| Password | `PHkvHfPPXgAUOBCygmQRYU8S!Aa1` |
| Encrypt | Optional — or Mandatory + ✅ **Trust server certificate** |

Gotchas that cause "can't connect":
1. **Comma vs colon**: `127.0.0.1:1433` fails silently; must be `127.0.0.1,1433`.
2. **Trust server certificate unchecked** (SSMS 19/20) → *"certificate chain … not trusted"* even with correct password.
3. Port only exists while the observability stack is up (`econgrader-db` publishes 127.0.0.1:1433).

Database name: **EconGrader**. Tables appear under Databases → EconGrader → Tables.

### CLI alternative (works even without the port publish)

PowerShell / CMD:
```
docker exec -it econgrader-db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "<SA_PASSWORD>" -d EconGrader
```

## 2. Web application

- Site: **http://localhost** (caddy proxy → frontend + api)
- First admin: on the login page use the bootstrap form with key:
  `b25d7f5a9c4ec0b7f9dde997b8fad4fe`
- After the first admin exists the bootstrap endpoint is typically disabled (clear `JWT_BOOTSTRAP_ADMIN_KEY` in `.env` + restart api).

## 3. Logs

- **Dozzle GUI**: http://localhost:9999 — pick container (`econgrader-api`, `econgrader-grading`, `econgrader-db`).
- Serilog rolling files (inside api container): `/app/logs/econ-grader-YYYYMMDD.log`
  ```
  docker exec econgrader-api ls /app/logs
  ```
- Raw stdout: `docker logs econgrader-api --tail 100`

## 4. Object storage (exam images)

- **Filebrowser GUI**: http://localhost:9998 — login `admin` / `admin`, **change immediately** (Settings → User Management).
- Same volume the services use: `app_storage` mounted at `/srv/storage/images`.

## 5. AI grading calls — Langfuse

- URL: http://localhost:3000 — create your admin account on first visit.
- API keys (for tracing from the grading service): *(not yet created — see §5 step 2)*
- Setup steps when regenerating:
  1. Langfuse → Settings → API Keys → Create.
  2. Put `LANGFUSE_PUBLIC_KEY` / `LANGFUSE_SECRET_KEY` into `.env`.
  3. `docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d grading`
- Health proof:
  ```
  curl http://localhost:8080/api/health
  docker logs econgrader-grading --tail 200
  ```

## 6. Where all secrets live

Everything is in **`.env`** (repo root, git-ignored). Current keys:
`SA_PASSWORD`, `DB_APP_PASSWORD`, `JWT_SIGNING_KEY`, `JWT_BOOTSTRAP_ADMIN_KEY`,
`GRADING_INTERNAL_KEY`, `ANTHROPIC_API_KEY`, `GOOGLE_API_KEY`,
`LANGFUSE_DB_PASSWORD`, `LANGFUSE_NEXTAUTH_SECRET`,
`LANGFUSE_PUBLIC_KEY`, `LANGFUSE_SECRET_KEY`.

Regenerate any secret: `openssl rand -base64 32` → edit `.env` → restart affected
container. Full tool guide: **OBSERVABILITY.md**.

## 7. Starting everything after a reboot

Open PowerShell in the project folder (`Desktop\econ_grader`) and run:
```
docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d
```
Plain `docker compose up -d` works too — just without the four GUI tools.
