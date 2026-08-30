# Observability & GUI Tools — Visual Guide

How to see (and edit) your database, logs, object storage, and AI API calls
with graphical tools. Everything here runs **locally only** — all ports are
bound to `127.0.0.1`, nothing is exposed to your network.

> 🔑 **Addresses, logins and passwords:** see `CREDENTIALS.md` (repo root).
> It is generated from `.env` and git-ignored because it holds real
> passwords — this file only documents *how*, never the secrets themselves.

## Starting the GUI stack

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d
```

(Plain `docker compose up -d` still works and starts *none* of the GUIs.)

| Tool | URL / Address | What you see |
|---|---|---|
| **SSMS / Azure Data Studio** | `localhost,1433` | Database: browse tables, edit rows visually |
| **Dozzle** | http://localhost:9999 | Live logs of all containers, search + follow |
| **Filebrowser** | http://localhost:9998 | Image storage: browse/upload/download/delete |
| **Langfuse** | http://localhost:3000 | Every AI grading call: prompt, response, tokens, cost |

---

## 1. Database (SQL Server) — SSMS or Azure Data Studio

The observability compose publishes the DB port to loopback only.

**Connection settings:**

| Field | Value |
|---|---|
| Server name | `localhost,1433` |
| Authentication | SQL Server Authentication |
| Login | `sa` |
| Password | value of `SA_PASSWORD` from `.env` |
| Trust server certificate | ✅ enable (SSMS 20 requires ticking this) |

- **SSMS**: https://aka.ms/ssms — full-featured, Windows-native
- **Azure Data Studio**: https://aka.ms/azuredatastudio — lighter, cross-platform

Once connected: expand **Databases → EconGrader → Tables**, right-click any
table → *Edit Top 200 Rows* to change data visually, or *Select Top 1000 Rows*
to browse. Right-click DB → New Query for arbitrary SQL.

> The app itself connects as least-privileged `econgrader_app`
> (`DB_APP_PASSWORD`), never as `sa`. Use `sa` for admin/maintenance only.

## 2. Logs — Dozzle

http://localhost:9999 lists every container; click one to stream its stdout.
Search box filters live output. Most useful streams:

- `econgrader-api` — .NET request logs + Serilog console sink
- `econgrader-grading` — Python JSON records per grading call
  (`provider`, `model`, `input_tokens`, `cost_usd`, …)
- `econgrader-db` — SQL Server container log

Serilog also writes rolling files into the `api_logs` volume
(`/app/logs/econ-grader-YYYYMMDD.log` inside `econgrader-api`):

```bash
docker exec econgrader-api ls /app/logs
docker exec econgrader-api tail -50 /app/logs/econ-grader-20260826.log
```

## 3. Object storage (uploaded exam images) — Filebrowser

http://localhost:9998 shows `/srv/storage/images` — the exact same volume and
path the API and grading service use, so what you see is what they see.
First login: **admin / admin** — change it immediately (Settings → User Management).

You can browse student submissions, download originals, delete junk, and watch
new uploads appear in real time.

> Deleting files here removes them permanently — there is no recycle bin.
> Prefer the app's own delete flow when unsure.

## 4. AI API calls — Langfuse

http://localhost:3000 — create the admin account on first visit.

### One-time setup (after first login)

1. In Langfuse: **Settings → API Keys → Create new API keys**
2. Copy `pk-lf-...` (public) and `sk-lf-...` (secret) into `.env`:
   ```
   LANGFUSE_PUBLIC_KEY=pk-lf-...
   LANGFUSE_SECRET_KEY=sk-lf-...
   ```
3. Restart grading so it picks the keys up:
   ```bash
   docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d grading
   ```

Every successful Claude grading call then appears as a trace named
`grade-answer` with prompt input, raw model output, token counts, latency,
AI score, and criteria count. Traces are batched by the SDK — allow a few
seconds after a grade before looking for them.

With keys unset, tracing is fully disabled and grading behaves exactly as before.

---

## What's wired where

- [docker-compose.observability.yml](docker-compose.observability.yml) — the
  four tools, git-ignored, local-only
- `grading-service/app/graders/claude_grader.py` — `_langfuse_client()` +
  `_trace_generation()` emit traces when keys are present
- `grading-service/app/config.py` — `LANGFUSE_*` settings
- `.env` — `LANGFUSE_DB_PASSWORD`, `LANGFUSE_NEXTAUTH_SECRET` (generated),
  plus the two API keys you add after creating a Langfuse account

## Removing it

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml down
docker volume rm econgrader_fb_database econgrader_langfuse_db_data   # optional wipe
```

The base stack keeps running untouched; only the GUI containers stop.
