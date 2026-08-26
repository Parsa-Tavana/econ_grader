# EconGrader — Production Readiness Plan

**Date:** 2026-08-26 · **Branch:** `Frontend/Parsa` · **Rev 2.1** — updated after pulling commits `2614514` (frontend JWT login) and `e481374` (9router gateway), plus Claude Code's post-login gap review. Rev 1 findings that are now obsolete are marked ✅ DONE.

---

## 0. Where the project stands now

| Area | Rev 1 status | Now |
|---|---|---|
| Backend JWT auth, roles, scope checks | ✅ | ✅ unchanged (`53f5f36`) |
| Frontend login page + route guards + logout | ❌ missing | ✅ **DONE** (`2614514`): `LoginPage`, `RequireAuth`, `RequireAdmin`, `Authorization: Bearer` interceptor, 401→/login redirect (loop-safe), header user badge with role + logout, en/fa strings |
| Legacy X-User-Id machinery removed from frontend | ❌ still active | ⚠️ **PARTIAL — and actively harmful now (Gap F1)**: WorkspacePage blocks grading submit behind a GUID dialog the backend ignores; a fresh logged-in user hits a dead-end wall |
| Admin Users page (list/create/deactivate accounts) | ❌ missing | ❌ **still missing — see Gap F2** |
| First-run bootstrap-admin UI | ❌ missing | ❌ **still missing — see Gap F3** |
| Token expiry handling | ❌ n/a | ⚠️ **Gap F4**: expiry stored but never checked |
| Role-aware navigation | ❌ n/a | ⚠️ **Gap F5**: all 7 nav links render for every role (desktop + mobile); Audit link visible but silently redirects |
| Role-aware actions / tailored role pages | ❌ n/a | ⚠️ **Gap F6**: admin/teacher mutations render for all roles → API 403s; no Student/Corrector UX yet (roadmap) |
| LoginPage correctness | ❌ n/a | 🐞 **Gap F7**: `navigate()` called during render (`LoginPage.tsx:26`) — must move to `useEffect` |
| Compose secrets / port exposure hardening | ❌ | ❌ unchanged — Phase 2 items all open |
| Deployment (domain, TLS, reverse proxy) | ❌ | ❌ unchanged — Phase 3 |

New in `e481374`: grading now routes through a **local 9router gateway**
(`ANTHROPIC_BASE_URL=http://host.docker.internal:20128/v1`, model renamed
`claude-econ`). This is a dev convenience with production consequences — see
**Gap B1**, which is a launch blocker for anything beyond your own machine.

### Verified details of what `2614514` actually did

- `api/auth.ts`: `login()` → POST `/auth/login`, stores `econgrader.token` + `econgrader.user` (JSON) in localStorage. Matches backend `LoginResponse` exactly.
- `client.ts`: request interceptor adds Bearer token; response interceptor clears session on any 401 and redirects to `/login?next=…`. **But it still also sends the legacy `X-User-Id` header "so old backend builds keep attributing actions"** and keeps exporting `setUserId/getUserId`.
- `WorkspacePage.tsx` **still blocks Accept/Override behind a manual GUID identity dialog** ("backend requires X-User-Id" comment) — this is now false: the backend takes the reviewer id from the JWT. Reviewers are being asked for an irrelevant GUID on every review.
- `SettingsPage.tsx` identity card still edits the legacy GUID instead of showing profile info.
- `App.tsx`: audit page admin-gated; everything else requires login. Sidebar not role-filtered (see F5).

---

## Phase 1 — Finish the auth integration (remaining ~40%)

**F1. Delete the legacy X-User-Id scheme.** Remove `setUserId/getUserId/currentUserId` and the X-User-Id line from the request interceptor in `client.ts`; remove the localStorage key `econgrader.userId`. Then:
   - `WorkspacePage.tsx`: drop the GUID identity dialog entirely — reviewer attribution comes from the JWT server-side (`AuditUserProvider`). The Accept/Override flow becomes one click.
   - `SettingsPage.tsx`: replace the identity editor with a read-only profile card (name/email/role from `getAuthUser()`).
   - Update i18n labels (`settings.userId*`, `settings.userIdHint` mention X-User-Id in en+fa) and `client.test.ts` if it touches the removed exports.
   - Note: `AuditPage.tsx` has its own local `userId` state for *filtering* the audit log by user — that's legitimate, keep it; only the identity-setting flow dies.

**F2. Admin Users page** (`/users`, `RequireAdmin`): wire GET/POST `/auth/users`, PUT `/auth/users/{id}` — table + create form + activate/deactivate with LAST_ADMIN conflict handling. Backend endpoints verified ready.

**F3. First-run bootstrap UI.** Nothing currently creates the first admin except curl. Add a collapsible panel on LoginPage calling POST `/auth/bootstrap-admin` ({bootstrapKey, email, password, displayName?}), shown only when explicitly expanded; surface BOOTSTRAP_DISABLED / BOOTSTRAP_CLOSED / EMAIL_TAKEN distinctly.

**F4. Token expiry.** Store `expiresAt = Date.now() + expiresInSeconds*1000` at login; treat a session as logged out once past expiry (in `isLoggedIn()`), so users get a clean login screen instead of 401 loops after 8h (default TTL 480 min).

**F5. Role-aware navigation (desktop AND mobile).** `AppLayout.tsx:23` renders all 7 `NAV_ITEMS` to every role — a Teacher sees the Audit link but gets silently bounced by `RequireAdmin` on click. Filter `NAV_ITEMS` by role via `getAuthUser().role`. **Both render sites must be filtered**: the desktop sidebar (`NAV_ITEMS.map`, line ~137) *and* the mobile top nav (`NAV_ITEMS.slice(0, 4).map`, line ~162) — filtering only the sidebar would leave Audit reachable in mobile view. Add a `roles?: UserRole[]` field per nav entry and a small `canSee(user, item)` helper.

**F6. Role-aware actions & page tailoring.** Beyond routes: teacher/admin-only mutations (create/delete exam, delete question, etc.) currently render for every logged-in role and fail with an API 403. Functional but rough UX. Minimum viable: hide destructive/admin-only action buttons when `user.role !== "Admin"`/`"Teacher"`. Tailored Student / Corrector experiences stay roadmap (no backend flows designed for them yet beyond scope checks).

**F7. LoginPage navigate-during-render bug.** `LoginPage.tsx:26` calls `navigate(next)` inside the render body for already-authenticated users — React warns about state updates during render and behavior can be flaky under StrictMode. Move into a `useEffect(() => { if (authed) navigate(next, {replace:true}); }, [authed, next])`.

## Phase 2 — Infrastructure readiness audit & hardening

### Audit result (verified against code, Rev 2.1)

| Component | Present? | Correct? | Gaps |
|---|---|---|---|
| **Database** — SQL Server 2022 container, named volume `sql_data`, EF auto-migrate (`MigrateAsync`, idempotent), healthcheck | ✅ | ⚠️ | SA password hardcoded in compose **and** `appsettings.json`; app connects as `sa` (no least-privilege user); port 1433 published to host; no backups; data lives only on your PC |
| **Object storage (answer images)** — `LocalFileStorage`: GUID keys, path-traversal guard, shared `app_storage` volume api⇄python at identical `/srv/storage/images`, atomic replace/delete | ✅ | ⚠️ | Images exist ONLY on your machine's disk — no backup, no second copy. Disk-backed impl is fine for pilot *if* backed up; S3-style object storage stays roadmap (interface `IFileStorage` ready for it) |
| **API logs** — Serilog console + rolling file `logs/econ-grader-YYYYMMDD.log`, 30-file retention, mounted to `api_logs` volume | ✅ | ✅ | Fine as-is |
| **Python logs** — JSON to stdout only, picked up by Docker | ✅ | ⚠️ | Docker's default `json-file` driver is **unbounded** → will eventually fill your disk. Needs `max-size` rotation in compose |
| **Secrets** — `.env` (git-ignored) feeds compose env vars | ✅ | ⚠️ | 🐞 **Critical subtlety:** `appsettings.json` ships a *committed dev JWT signing key* as fallback. `Program.cs` throws only when the key is *missing* — but it never is, because the file always supplies the weak default. Run without `JWT_SIGNING_KEY` env and prod silently signs tokens with a publicly-known key → anyone can forge an Admin token. Needs a Production-mode guard rejecting the literal dev default |
| **Service-to-service auth (.NET ⇄ Python)** — `GradingClient.InternalKey` config property exists… | ⚠️ | ❌ | …but is **never read or sent anywhere**, and Python `/grade` has no auth at all. Unpublishing :5001 (planned) gives network isolation only; a shared internal header should be implemented end-to-end |
| **Resilience** — api waits on db+grading healthchecks before starting | ✅ | ⚠️ | No `restart: unless-stopped` on ANY service — containers stay down after reboot/OOM |
| **Backups / restore** | ❌ | — | Nothing exists: no scripts, no schedule, no documented restore. Required before real student data lands |
| **Root `.env.example`** | ❌ | — | Only `grading-service/.env.example` exists; root secrets are undocumented |
| **Monitoring** — `/api/health` reports API + grading status | ✅ | ⚠️ | Endpoint good; nothing consumes it externally yet (uptime monitor comes with deployment phase) |

### Fixes (all open)

1. **Secrets out of compose & code**: parameterize `SA_PASSWORD` (incl. inside the db healthcheck); **rotate it — it's in git history twice over** (compose + appsettings). Root `.env.example` documenting every variable.
2. **Dev-default JWT key guard**: in Production environment, refuse to start if `Jwt:SigningKey` equals the committed dev default (fail-fast, not silent weakness).
3. **Unpublish internal ports**: db :1433 and grading :5001 bind nothing on the host; api binds `127.0.0.1:8080:8080`.
4. **Internal service auth**: wire `InternalKey` end-to-end — .NET sends `X-Internal-Key` header from config; FastAPI `/grade` (and siblings) verify via dependency; compose sets matching secret in both services.
5. **Restart policies**: `restart: unless-stopped` on db, grading, api (proxy/frontend when added).
6. **Docker log rotation**: `logging: {driver: json-file, options: {max-size: "10m", max-file: "3"}}` per service.
7. **Least-privilege DB user**: create `econgrader_app` login/user with db-owner rights on `EconGrader` only (via init script or documented sqlcmd step); app connection string stops using `sa`.
8. **SPA production serving**: multi-stage frontend image (nginx) + SPA fallback routing behind the proxy.
9. **Reverse proxy correctness**: `UseForwardedHeaders`, HSTS outside Development, TLS at proxy.
10. **Rate limiting** on `/api/auth/*` (~10/min/IP → 429); explicit CORS origins (never `*`) or none under same-origin.
11. **Backups**: nightly script — SQL dump + tar of `app_storage` into dated folder, retention policy, documented RESTORE steps (an untested restore is not a backup).
12. Minor cleanup: python service's `depends_on: db` is vestigial (it never touches SQL Server) — remove.

## Phase 3 — Deploy on the `.dev` domain (unchanged direction)

Recommendation stands: **Cloudflare named Tunnel from your own machine first** (extends share-demo.ps1 toolchain; free TLS satisfies `.dev`'s HSTS-preload which forbids HTTP entirely), VPS+Caddy documented as option B. Confirm with your reseller that you get DNS control (CNAME into `<tunnel-id>.cfargotunnel.com`).

## Phase 4 — Pre-launch gates & ops

Same as Rev 1: full click-through gates, seed/smoke script, AI cost guardrails, minimal privacy notice for student data, then the M-item roadmap (evaluation filters, image viewer, DOCX extraction).

### NEW — B1. The 9router gateway must be production-resolved (blocker)

`e481374` makes every Claude grading call go to `http://host.docker.internal:20128/v1` — a proxy running on your PC (the Claude desktop app's local gateway), using a custom model name `claude-econ`. Consequences:

- It works only while your machine runs that gateway. In any hosted deployment (VPS, another PC, or even Docker on a different network namespace where `host.docker.internal` isn't mapped), **all Claude grading fails instantly**.
- Extra env vars (`extra_headers`) may be required by the gateway; the Anthropic SDK reads `ANTHROPIC_BASE_URL` automatically, so no code change was needed — but nothing documents this dependency.
- Options for production:
  - **A (simplest):** make the compose override optional again — keep direct `api.anthropic.com` as default via a real `ANTHROPIC_API_KEY` in prod `.env`, and move the 9router settings into a `docker-compose.override.yml` (git-ignored pattern already in your .gitignore) for local dev. Model name reverts to a real model id.
  - **B:** run 9router (or equivalent gateway) as a compose service on the server too, keeping `claude-econ` routing — only if you specifically need the gateway's billing/routing features in prod.
  - Either way: document it in PROJECT_MAP §7 and verify a live grading run post-deploy (the seed script in Prompt 3 does exactly this).

---

## Updated sequencing

| Week | Milestone |
|---|---|
| 1 | Phase 1 remainder: F1–F7 (smaller now — mostly deletion + two pages + expiry + nav/UX polish) |
| 2 | B1 decision + Phase 2 hardening (secrets rotation, ports, SPA serving, proxy) |
| 3 | Phase 3: tunnel/VPS up, `.dev` over HTTPS, seed script green |
| 4 | Pilot with real teachers |

`CLAUDE_CODE_PROMPTS.md` has been updated to match (Rev 2.1): Prompt 1 is now "finish the job" — F1–F7 + the 9router fix (B1), with acceptance gates extended to cover role-filtered nav on desktop *and* mobile and a zero-React-warnings console check. Prompts 2–3 unchanged.
