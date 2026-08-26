# Claude Code Prompts — Production Phase (EconGrader) · Rev 2.1

Four sequenced prompts, updated after commits `2614514` (frontend JWT login landed),
`e481374` (Claude grading routed through local 9router gateway), and a full
infrastructure-readiness audit (database / storage / logs / secrets). Run them **in
order** from the repo root (`Frontend/Parsa`). Each prompt is self-contained: verified
contracts, invariants, blocking acceptance gates. Commit between prompts.

> **Rev 2 → Rev 2.1 changes:** Prompt 1 extended with the four gap-review findings
> (role-filtered nav desktop+mobile, action-level gating, LoginPage render bug).
> NEW PROMPT 2B covers infrastructure readiness: secrets/DB user, internal service
> auth wiring (InternalKey is declared but never sent; Python has no auth), restart
> policies, Docker log rotation, backups with restore docs. Old Prompt 2's items that
> moved into 2B are marked; run order is now 1 → 2A → 2B → 3.

---

## PROMPT 1 — Finish frontend auth integration + resolve 9router coupling

```
CONTEXT
Repo: EconGrader (React 18+Vite+TS in ./frontend, .NET 9 API in ./src).
JWT auth is done on both ends: backend since 53f5f36; frontend since 2614514
(LoginPage at /login with ?next= support, RequireAuth/RequireAdmin route guards,
Bearer request interceptor, 401→/login redirect in client.ts, header user badge +
logout, en/fa i18n under auth.* / user.*).

WHAT'S STILL WRONG (all verified by inspection)
A. Legacy identity scheme coexists with JWT — AND IS NOW HARMFUL:
   - client.ts still exports setUserId/getUserId/isValidGuid, keeps localStorage
     "econgrader.userId", and the request interceptor still sends "X-User-Id"
     ("so old backend builds keep attributing actions" — no longer true: backend
     derives identity ONLY from the JWT via AuditUserProvider).
   - WorkspacePage.tsx (~line 180) blocks Accept/Override submit until the user
     pastes a valid GUID into a manual dialog claiming "backend requires X-User-Id"
     — false since 53f5f36. A freshly logged-in user hits a dead-end wall in the
     core grading workflow. Remove the dialog; attribution comes from the JWT.
   - SettingsPage.tsx has a GUID identity editor instead of profile display.
   - i18n labels settings.userId* / settings.userIdHint reference X-User-Id.
B. No admin Users page although backend exposes GET/POST /api/auth/users and
   PUT /api/auth/users/{id} [Admin only].
C. No first-run UI for POST /api/auth/bootstrap-admin ({bootstrapKey,email,
   password,displayName?} → LoginResponse | 403 BOOTSTRAP_DISABLED|BOOTSTRAP_CLOSED |
   401 INVALID_CREDENTIALS | 409 EMAIL_TAKEN).
D. Token expiry ignored: auth.ts stores expiresInSeconds but isLoggedIn() only
   checks token presence → users hit 401 loops after TTL (default 480 min).
E. Nav is not role-filtered: AppLayout.tsx NAV_ITEMS (~line 23) renders ALL 7 links
   (incl. Audit) to every role; a Teacher clicking Audit gets silently redirected
   by RequireAdmin. BOTH render sites must be filtered — the desktop sidebar
   (~line 137 NAV_ITEMS.map) AND the mobile top nav (~line 162
   NAV_ITEMS.slice(0,4).map), or Audit stays reachable on phones.
F. LoginPage.tsx (~line 26) calls navigate(next) DURING RENDER for already-authed
   users → React "state update while rendering" warning; flaky under StrictMode.
   Move to useEffect keyed on [authed, next].
F. docker-compose.yml routes Claude grading through a LOCAL 9router gateway
   (ANTHROPIC_BASE_URL=http://host.docker.internal:20128/v1, CLAUDE_MODEL=claude-econ).
   Works only on this machine while that desktop proxy runs. For production the
   default must be direct api.anthropic.com again, with the gateway moved to an
   optional override file.

VERIFIED CONTRACTS (do NOT change backend)
- POST /api/auth/login {email,password} → {accessToken,tokenType:"Bearer",
  expiresInSeconds,user:{id,email,displayName,role,isActive,createdAt}}
  | 401 {code:"INVALID_CREDENTIALS"}   [already wired]
- POST /api/auth/bootstrap-admin — see above codes.
- GET /api/auth/users → ManagedUserDto[] {id,email,displayName,role,isActive,createdAt}
- POST /api/auth/users {email,password,displayName,role} → 201 ManagedUserDto
  | 409 EMAIL_TAKEN | 400 WEAK_PASSWORD (<8 chars) | 400 INVALID_ROLE
- PUT /api/auth/users/{id} {isActive?,displayName?,role?} → 200 | 409 LAST_ADMIN
  (also returned when demoting the last active admin)
- Role strings exactly "Teacher"|"Admin"|"Corrector"|"Student".
- Reviewer attribution server-side from JWT claims — no body/header needed.

TASKS
1. Purge legacy scheme: remove setUserId/getUserId/currentUserId/X-User-Id header
   logic and the "econgrader.userId" storage key from client.ts (keep isValidGuid if
   AuditPage's filter uses it). WorkspacePage: delete the identity dialog state and
   validation — Accept/Override submit directly, no GUID prompt. SettingsPage:
   replace identity card with read-only profile (name/email/role badge from
   getAuthUser()). Update affected i18n keys in BOTH en.ts and fa.ts (add new ones
   like settings.profile*, remove obsolete userIdHint text or repurpose).
2. Admin UsersPage (/users): table of accounts + create form (role select of the four
   roles) + activate/deactivate toggle with confirm; friendly LAST_ADMIN /
   EMAIL_TAKEN / WEAK_PASSWORD errors. Add to i18n en+fa. Gate route with RequireAdmin
   AND hide the nav link for non-admins (see task 5).
3. LoginPage: add collapsible "First-time setup" panel → bootstrap-admin call;
   distinct messages for BOOTSTRAP_DISABLED ("ask your administrator"), BOOTSTRAP_
   CLOSED, INVALID_CREDENTIALS, EMAIL_TAKEN. Do not auto-probe; expand-only.
4. Session expiry: store expiresAt = Date.now()+expiresInSeconds*1000 in auth.ts;
   isLoggedIn() returns false when expired and clears the session; client.ts 401
   handler stays as backstop.
5. Role-filter NAV_ITEMS: add optional `roles?: string[]` per entry + canSee(user,
   item) helper; apply at BOTH render sites in AppLayout.tsx — desktop sidebar map
   AND mobile top-nav slice(0,4) map. Minimum: Audit visible to Admin only;
   structure so future per-role entries are one-line changes.
6. Fix LoginPage navigate-during-render: move the already-authenticated redirect
   into useEffect(() => { if (authed) navigate(next, {replace:true}); }, [authed,
   next]); render the form (or null) otherwise. No React warnings in console.
7. Action-level role gating (light touch): hide admin-only/destructive buttons
   (exam/question delete, user management entry points) when the logged-in role
   lacks permission — mirror backend [Authorize(Roles=…)]. Do NOT build separate
   Student/Corrector page layouts yet; just don't render actions they can't use.
8. Compose/9router decoupling: restore direct-Anthropic defaults in docker-compose.yml
   (MODEL_NAME=<real model id>, remove ANTHROPIC_BASE_URL/CLAUDE_MODEL overrides),
   move the current 9router values into docker-compose.override.yml (already
   git-ignored) with a comment explaining host.docker.internal + port 20128 and when
   to use it. Document both modes in PROJECT_MAP.md §7.

INVARIANTS (PROJECT_MAP §1)
- Blind grading: teacher scores never appear in any AI-grading request.
- GradingRun immutability, append-only reviews/audit unchanged.
- Existing tests pass except tests of removed exports (update them).

ACCEPTANCE GATES (blocking — do not declare done until green)
- npm run lint → 0 errors; npm test → all pass; npm run build → succeeds
- grep -rn "X-User-Id\|econgrader.userId" frontend/src → only AuditPage filter hits
  (its own local state), none elsewhere
- Browser console shows NO React warnings on: visiting /login while logged in,
  grading workspace review flow, nav rendering as Teacher vs Admin
- As a Teacher: Audit link absent from BOTH desktop sidebar and mobile nav; direct
  URL /audit still redirects; no admin-only action buttons visible anywhere
- docker compose config resolves without plaintext gateway URL in base compose
- Print a short manual test script: start stack → bootstrap first admin via new UI →
  login as Teacher → review an answer with NO GUID prompt appearing → login as Admin
  → create a user on /users.
Report file-by-file changes and any spec deviations.
```

---

## PROMPT 2A — Web hardening & production packaging

Run after Prompt 1 is merged.

```
CONTEXT
Same repo, post-auth-integration. Target deployment: docker compose behind a reverse
proxy on a public .dev domain (.dev is HSTS-preloaded — HTTPS mandatory). Verified
problems (infrastructure items live in Prompt 2B — do NOT do them here):
- Nothing serves the built SPA in production (frontend/dist exists, unused in prod).
- Program.cs lacks UseForwardedHeaders (audit IPs will show proxy IPs) and HSTS.
- No rate limiting on /api/auth/* (brute-force surface: login + bootstrap-admin).
- CORS falls back to AllowAnyOrigin when Cors:AllowedOrigins unset/"*".

TASKS
1. docker-compose.yml: add caddy:2 `proxy` service on 80/443 routing ${SITE_ADDRESS}:
   /api/* → api:8080, everything else → frontend service.
2. New frontend/Dockerfile multi-stage (node:20-alpine build → nginx:alpine serve) +
   nginx.conf (gzip; SPA try_files fallback; no-cache index.html; immutable cache for
   hashed assets); wire as internal `frontend` service behind caddy.
3. Program.cs: UseForwardedHeaders(XForwardedFor|XForwardedProto) FIRST in pipeline
   with KnownNetworks/KnownProxies cleared per standard container pattern; UseHsts()
   outside Development; AddRateLimiter fixed-window per-IP ~10 req/min on
   /api/auth/login and /api/auth/bootstrap-admin → 429.
4. CORS default-deny posture: unset Cors:AllowedOrigins must NOT fall back to
   AllowAnyOrigin — log a warning instead (same-origin deployments need no CORS).
5. Update PROJECT_MAP.md §7/§8 to match reality afterwards.

CONSTRAINTS
- No API contract/entity/migration changes.
- Keep local dev flow intact: start.ps1/start.bat, Vite :5173→:8080 proxy, dotnet run
  against localhost SQL. Document any new dev env vars.
- The shared app_storage volume between api & grading containers with IDENTICAL
  FileStorage__RootPath / IMAGE_STORAGE_ROOT (/srv/storage/images) MUST stay
  byte-identical — absolute image paths cross the .NET→Python boundary.
- Do not disturb the 9router override pattern added in Prompt 1 task 8.
- Secrets/ports/restart/logs/backups are Prompt 2B's job — skip them here.

ACCEPTANCE GATES (blocking)
- dotnet build → 0 errors 0 warnings
- npm run lint && npm test && npm run build → green
- cd grading-service && python -m pytest tests -q → 15 passed
- docker compose config → valid
- docker compose up --build → all healthy; curl http://localhost/api/health reports
  grading up; http://localhost/ serves the SPA
```

---

## PROMPT 2B — Infrastructure readiness: secrets, DB user, service auth, ops

Run after Prompt 2A is merged. This makes the stack safe to leave running and safe
to lose a disk to.

```
CONTEXT
Same repo. Everything currently runs in Docker on one machine. Audit findings (all
verified against code):
SECRETS
- SA_PASSWORD "YourStrong@Passw0rd" hardcoded in docker-compose.yml AND inside the
  db healthcheck command AND in src/EconGrader.Web/appsettings.json. In git history.
- appsettings.json ships a committed DEV JWT signing key as fallback. Program.cs
  throws when Jwt:SigningKey is missing — but the file always supplies the weak
  default, so running prod without JWT_SIGNING_KEY silently signs tokens with a
  PUBLICLY KNOWN key → anyone can forge an Admin JWT. This must fail-fast.
- No root .env.example exists (only grading-service/.env.example).
SERVICE-TO-SERVICE AUTH
- GradingClient.InternalKey config property EXISTS but is never read/sent; Python
  /grade (and /prompts, /evaluate) have NO auth at all. Port-unpublishing alone is
  network-level only defense.
OPS
- NO restart policies on any compose service.
- Python logs JSON to stdout; default json-file driver is UNBOUNDED → disk fill risk.
- db port 1433 and grading port 5001 published to host.
- App connects to SQL Server as sa (least privilege violated).
- NO backups of any kind; answer images exist only on local disk.
- grading service has `depends_on: db` but never touches SQL Server (vestigial).

TASKS
1. Secrets:
   - compose: SA_PASSWORD from ${SA_PASSWORD}; healthcheck uses $$ expansion so it
     reads the env too.
   - appsettings.json: remove the real-looking dev values from Connection string
     password and Jwt:SigningKey (leave empty or placeholder); keep dev flow working
     via appsettings.Development.json or user-secrets documented in README.
   - Program.cs: after binding Jwt:SigningKey, if environment is Production AND key
     equals the known dev literal "dev-only-signing-key-change-me-in-production…",
     throw at startup with instructions. Same guard pattern for empty keys.
   - Create root .env.example documenting EVERY variable used by compose (ANTHROPIC_
     API_KEY, GOOGLE_API_KEY, JWT_SIGNING_KEY, JWT_BOOTSTRAP_ADMIN_KEY, SA_PASSWORD,
     SITE_ADDRESS, ACME_EMAIL, GRADING_INTERNAL_KEY) with generation hints
     (openssl rand -base64 48).
2. Internal service auth end-to-end:
   - .NET: bind GradingService:InternalKey from GRADING_INTERNAL_KEY; GradingClient
     adds header X-Internal-Key: <key> to every request when configured.
   - Python: FastAPI dependency verifying X-Internal-Key against settings.GRADING_
     INTERNAL_KEY on /grade, /evaluate*, /prompts*; open /health stays open (for
     uptime checks + Docker healthcheck). Empty/missing key => reject with 401
     unless settings allows dev mode (ENVIRONMENT != production), so local bare-metal
     dev without the key still works.
   - compose: set GRADING_INTERNAL_KEY=${GRADING_INTERNAL_KEY} on both services.
3. Ports & restart & logs:
   - Remove host publishing for db (1433) and grading (5001); api binds
     127.0.0.1:8080:8080.
   - restart: unless-stopped on db, grading, api.
   - logging driver json-file with max-size "10m", max-file "3" on every service.
4. Least-privilege DB user:
   - Add scripts/init-db.sql (or entrypoint init): create login econgrader_app with
     ${SA_PASSWORD}-independent password ${DB_APP_PASSWORD}, user in db_owner of
     EconGrader ONLY (EF migrations need DDL), nothing else. Compose api connection
     string switches to econgrader_app. sa remains for admin/maintenance only.
5. Backups:
   - scripts/backup.ps1 + backup.sh: sqlcmd BACKUP DATABASE to file + tar the
     app_storage volume (docker run --rm -v … tar) into ./backups/YYYY-MM-DD-HHmm/;
     retention: keep last N (param, default 14); print sizes.
   - scripts/restore.md: step-by-step RESTORE DATABASE + volume untar + verification
     query. An untested restore path is not a backup — include a verify command.
6. Remove vestigial depends_on: db from the grading service (it never queries SQL).

CONSTRAINTS
- No entity/migration changes; no API contract changes beyond the new optional
  internal-key header between our own two services.
- Local dev must still work WITHOUT setting GRADING_INTERNAL_KEY (dev bypass above);
  document this in .env.example comments.
- Do not touch the 9router override pattern or frontend serving (Prompt 2A scope).
- Never echo secrets in scripts; scripts fail loudly if required vars unset.

ACCEPTANCE GATES (blocking)
- dotnet build → 0 errors 0 warnings; pytest -q → 15 passed; npm gates untouched.
- grep for "YourStrong@Passw0rd" across repo → ZERO hits outside git history.
- Program.cs startup in Production with dev-default Jwt:SigningKey → refuses to boot
  with clear error (test via ASPNETCORE_ENVIRONMENT=Production dotnet run attempt).
- python -c test or curl: /grade without X-Internal-Key → 401 in prod-mode; with
  correct key → passes validation (mock grader acceptable).
- docker compose config → valid, no plaintext passwords; db/grading expose NO host
  ports; all services show restart policy + log rotation options.
- Fresh clone simulation: cp .env.example .env, fill placeholders, docker compose up
  --build → healthy stack; kill -9 the api container → it auto-restarts.
- Run backup script once → backups dir contains .bak + storage archive; restore.md
  steps reference exactly those filenames.
Report file-by-file changes; list every variable added to .env.example.
```

---

## PROMPT 3 — Deployment scaffolding (Cloudflare Tunnel primary, VPS variant)

Run after Prompt 2.

```
CONTEXT
Same repo. Goal: publish on grader.<DOMAIN>.dev. .dev is HSTS-preloaded by Google —
valid HTTPS mandatory, no HTTP fallback ever. Primary path: app runs on the owner's
Windows machine exposed via a NAMED Cloudflare Tunnel (upgrade of existing
share-demo.ps1 quick-tunnel toolchain). VPS+Caddy variant documented as alternative.
Domain bought via an Iranian reseller — docs must cover confirming DNS control
(CNAME <tunnel-id>.cfargotunnel.com) or delegating DNS to Cloudflare nameservers.
NOTE: Claude grading may route through a local 9router gateway in dev
(docker-compose.override.yml); production uses direct Anthropic API — the seed script
must verify a live grading run works against whatever provider config is deployed.

TASKS
1. deploy/cloudflared/config.yml template: tunnel ID, ingress mapping
   grader.<domain>.dev → http://localhost:8080 (the loopback-published API), optional
   second hostname for LAN use; credentials-file path documented.
2. deploy/cloudflared/SETUP.md: PowerShell commands — cloudflared tunnel
   login/create/route dns/install-as-service; where credentials land; DNS options
   (CNAME vs nameserver delegation via reseller); rollback/removal steps.
3. deploy/vps/: README.md + docker-compose.prod.yml — same services, Caddy terminates
   TLS automatically using ${SITE_ADDRESS} and ${ACME_EMAIL}; UFW baseline
   (22/80/443 only); prominent note: test VPS IP reachability from Iran BEFORE
   committing to a long plan.
4. deploy/seed.ps1 (+ .sh): idempotent smoke script — wait for /api/health; bootstrap
   admin if none exists (key from env; then advise emptying it to disable bootstrap);
   optionally seed demo exam/question/student via authenticated API calls using the
   admin token; run ONE real grading call; print PASS/FAIL summary. Never echo secrets.
5. Root DEPLOY.md: prerequisites (openssl rand -base64 48 secrets, ROTATED sa
   password, JWT keys, bootstrap key lifecycle), Option A tunnel vs Option B VPS
   decision table, go-live order: secrets → compose up → healthcheck → seed → DNS →
   watch logs 24h.
6. Optional GitHub Actions workflow: on push to main run dotnet build, pytest,
   npm lint/test/build; registry push gated behind ENABLE_DEPLOY variable.

CONSTRAINTS
- Scripts idempotent, re-runnable, never log secrets.
- Docs in English with a short Farsi summary at the end of each file.
- share-demo.ps1 stays untouched.

ACCEPTANCE GATES (blocking)
- All referenced files exist; bash -n clean; PowerShell parses via
  pwsh -NoProfile -Command "[scriptblock]::Create((Get-Content -Raw file))"
- SETUP.md commands match current cloudflared CLI behavior
  (tunnel create/route dns/run/install service)
- DEPLOY.md contains a dry-run walkthrough a stranger could follow end-to-end.
```

---

## How to drive each session

1. **One prompt per session**, commit between. Run order: **Prompt 1 → 2A → 2B → 3** (2B is the infrastructure pass; it can also swap with 2A if you want ops safety first, they're independent by design). Suggested commit messages: `refactor(auth): drop legacy X-User-Id…`, `feat(web): SPA serving + proxy + rate limits`, `chore(infra): secrets, db user, service auth, backups`, `feat(deploy): tunnel/vps scaffolding`.
2. **Gates are blocking**: instruct Claude Code not to finish until every gate passes; if one can't run in your environment (e.g., Docker), require it to say so explicitly rather than skip silently.
3. **Review diffs before merging** — especially Prompt 2A/2B security changes.
4. Anti-drift reply if it wanders: *"Stop. Re-read TASKS and CONSTRAINTS. List any deviation before continuing."*
5. After Prompt 1, re-verify blind grading manually: DevTools → Network → run AI grading → confirm no teacher score appears anywhere in the request chain.
6. After Prompt 2B, do one real restore drill from `scripts/restore.md` before trusting the backups.
