<!-- ============================================================================
HOW TO LAUNCH (notes for SHPA-N6 — not part of the prompt itself):

1. This session needs browser control. In the repo root, once:
     claude mcp add playwright -- npx '@playwright/mcp@latest'
2. Start nothing beforehand — the prompt makes Claude Code bring the stack up itself.
3. Run `claude` in the repo root, then paste everything BELOW the divider line.
============================================================================ -->

# EconGrader — Full E2E QA & Auto-Fix Session (QA-1 · Rev 1)

## Your role

You are a senior QA engineer AND a full-stack developer on this project. Today you act as a
demanding, professional user-acceptance tester: you will exercise **every** user-facing feature
of EconGrader the way a real teacher would (plus all the ways a real user misuses it), log every
defect you find, then **fix every confirmed defect at the root cause**, re-verify each fix, and
finish with a formal QA report. You have full autonomy to edit code in this session.

## Mission, in order

1. Bring the full stack up and establish a healthy baseline.
2. Map the feature surface (pages, endpoints, roles) from code.
3. Attack the REST API directly (auth, validation, permissions, immutability rules).
4. Drive the real UI end-to-end with the Playwright MCP — every page, every journey, happy paths and failure paths.
5. Deep-test the core pipeline: uploads → rubrics → AI grading runs → reviews → evaluation metrics.
6. Cross-cutting checks: i18n (EN/FA), session expiry, refresh persistence, double-submit, concurrency, error states.
7. Fix everything you confirmed, grouped into clean commits.
8. Re-run every repro script as a regression pass, then write `QA_REPORT.md`.

## Operating context

- Work in the current directory (repo root). Confirm branch with `git status`; **do not switch branches**. Expected: `Frontend/Parsa`.
- Stack: React 18 + Vite frontend on **:5173** (proxies `/api` → backend), ASP.NET Core .NET 9 API on **:8080**, Python FastAPI grading-service on **:5001** (internal only, must never be exposed), SQL Server via docker compose on **:1433**.
- Start everything with `start.ps1` / `start.bat`. Health checks: `GET http://localhost:8080/api/health` (must report the grading dependency healthy) and the grading service's `/health`.
- Auth is JWT-based via the login page. Seeded user credentials: search `scripts/`, `deploy/`, and seed-related files. If no credentials are documented, obtain test users through whatever admin/seed mechanism exists and record exactly what you did.
- **Upload types are enforced in THREE places that must stay in sync**: `FileUploadValidator.cs` (.NET), `FileAttachment.tsx` + `accept` attributes in pages (frontend), `attachments.py` (Python, raises ValueError on unknown extensions). Any fix touching uploads must update all three layers plus the i18n error strings in `en.ts` / `fa.ts`.
- PowerShell 5.1 gotcha seen in this repo: with `$ErrorActionPreference='Stop'`, native tools writing to stderr (e.g. cloudflared) become terminating errors — wrap native calls in helpers that relax EAP. Keep `.ps1` edits ASCII-safe / BOM-aware.

## Non-negotiable invariants — a fix that violates these is a FAILED fix

1. **Blind grading:** teacher scores are NEVER sent to the AI. The Python `/grade` contract has no teacher-score field; `TeacherScoreSnapshot` is written only AFTER the AI responds. Preserve and prove this.
2. `GradingRun` rows are immutable evidence records — never edited after creation.
3. `TeacherReview` rows are append-only (accept/override creates a NEW row; history is never mutated).
4. `AuditLog` is append-only.
5. Exactly one active rubric per question; new versions supersede, never delete old ones.
6. One answer per (student, question) — uniqueness must surface as a friendly error, not a 500.
7. The browser talks ONLY to the .NET API via the `/api` proxy; the grading service stays internal.
8. Never print, commit, or store secrets (`.env` values, JWT signing keys, provider API keys). Redact them anywhere they'd appear in logs, reports, or evidence files.

## Ground rules

- **Live AI budget: at most 10 real grading calls this entire session.** Before each live call, capture the cost estimate the system provides. Prefer single-question runs over batches. All other grading scenarios must use a mocked/stubbed provider (see Phase 4). If you would exceed the budget, stop and ask.
- **Fix autonomy:** you may fix anything you have reproduced and confirmed, at any severity. No symptom-masking: no swallowing exceptions, no hiding broken UI behind conditionals, no deleting failing assertions instead of fixing causes.
- **Repro discipline:** for every bug, write down exact reproduction steps (and a curl command or Playwright snippet where possible) BEFORE fixing. After fixing, re-run the exact same repro to verify. Keep a running list of fixed-bug repros — this becomes the final regression pass.
- **Commits:** small and logical (`fix(api): …`, `fix(ui): …`, `test: …`). Never commit `qa-artifacts/`, secrets, or generated binaries. Update `PROJECT_MAP.md` if a fix changes documented behavior.
- **Evidence:** screenshots, request/response captures, and fixtures go in `qa-artifacts/` (create it, add to `.gitignore`). Reference evidence files from the bug ledger.
- **Checkpoints:** after each phase, post a compact summary (bugs found, bugs fixed, what's next) and continue autonomously. Stop and ask me only if you're blocked on something only I can decide: exceeding the live-call budget, changing an invariant, or destructive data operations beyond the test dataset.

## Tooling notes

- **Playwright MCP is your primary UI instrument.** Pattern: `browser_navigate` → `browser_snapshot` (the accessibility tree is your eyes — cheaper and more precise than screenshots) → interact via element refs. Take screenshots only to document visual bugs. After EVERY page load, read console messages — a console error counts as a bug even if the UI looks fine. After every mutation, check network requests for unexpected 4xx/5xx.
- Test the API layer with curl (or Invoke-RestMethod) — it's faster than the UI for edge-case matrices; the UI layer proves the wiring.
- Generate test fixtures programmatically with Python into `qa-artifacts/fixtures/`: PNG/JPG images containing drawn text (Pillow), PDFs (reportlab/fpdf), `.xlsx` (openpyxl), plus plain `.txt`/`.csv`. Make images look like plausible scanned handwritten answers (dark text on light background).
- When a displayed number looks doubtful (metrics, totals, costs), compute ground truth yourself — query SQL Server via `docker exec` + sqlcmd, or replicate the calculation in Python — and compare.

---

## PHASE 0 — Baseline & environment

- [ ] `git status` clean-ish; note HEAD commit. Start the stack; wait for all services healthy; record versions.
- [ ] Walk the health checks above. If the stack won't start, diagnosing THAT is the first task — a repo-caused startup failure is itself a finding (S2).
- [ ] Log in via the UI with seeded credentials. Confirm token storage, redirect behavior, and logout.
- [ ] Snapshot the DB row counts of main tables (exams, questions, answers, runs, reviews, users) so you can prove at the end that testing didn't corrupt data.

## PHASE 1 — Feature-surface recon (code reading, fast)

- [ ] Enumerate routes/pages in `frontend/src/pages/` (Login, Dashboard, Exams, ExamDetail, Workspace, Queue, Evaluation, Students, StudentDetail, Audit, Users, Settings) and map each to its API endpoints.
- [ ] Enumerate controllers/routes in the .NET API and the Python service routes. Note anything reachable via API but absent from the UI (potential missed features) and vice versa.
- [ ] Note role model (roles enum, role-gated navs, `[Authorize]` attributes) so Phase 3 can test permission boundaries.
- [ ] Static red-flag pass only (don't deep-audit): TODO/FIXME comments, obvious `catch {}`, disabled validations, hardcoded IDs.

## PHASE 2 — API contract testing (before touching the UI further)

- [ ] **Unauthenticated access:** every endpoint must 401 appropriately. Find any leak (health endpoints excepted by design).
- [ ] **Auth abuse:** garbage/expired/tampered tokens; token from user A used on user B's resources (IDOR spot-checks on exams, answers, runs); legacy `X-User-Id` header must be IGNORED now that JWT exists — try forging it.
- [ ] **Role boundaries:** teacher-role token hitting admin-only endpoints (e.g. user management) must fail cleanly.
- [ ] **Validation abuse:** missing/oversized/wrong-type fields; negative or absurd numbers (MaxScore = -5, Question Number = 0 or duplicates within an exam); unicode + 10k-char strings; empty arrays for rubric criteria.
- [ ] **Invariant probes:** POST a second answer for the same (student, question) → expect friendly 409-style error; edit attempts against run/review/audit resources → expect refusal; rubric versioning behavior (v2 supersedes v1, v1 still readable, exactly one active).
- [ ] Record status codes + response bodies. Any 500 where a 400 was appropriate = bug.

## PHASE 3 — UI end-to-end journeys (Playwright)

Drive these as a real user would, in ENGLISH first. For each journey: happy path + at least two failure/edge variants. Console and network checked throughout.

- [ ] **Auth:** login with wrong password (friendly error?), empty fields, logout, back-button after logout, protected-route redirect when logged out, session-expiry mid-use (wait/simulate) → must land on login without crashing.
- [ ] **Exams:** create exam (normal, missing fields, duplicate name), rename/edit, delete (if supported) with cascade awareness, list sorting/searching, empty-state rendering on a fresh filter.
- [ ] **Questions:** add questions (duplicate numbers, blank text, MaxScore edge values), edit, ordering display.
- [ ] **Rubric builder:** create criteria (empty criterion id/text, MaxScore ≤ 0, huge order values), total-max computation shown matches sum, version bump flow, activating/superseding versions, UI behavior when NO active rubric exists.
- [ ] **Students:** add students (duplicate ExternalId like "S001"), edit display names, detail page aggregates make sense.
- [ ] **Workspace (uploads):** see the full matrix in Phase 4 — drive it through the UI here.
- [ ] **Queue / grading runs:** enqueue runs, observe state transitions, cancel if supported, double-click protection on the run button, behavior when grading service is unhealthy.
- [ ] **Review flow:** accept AI score; override with a different score + comment; verify history view shows BOTH entries appended (old one untouched); metrics reflect overrides afterward.
- [ ] **Evaluation page:** metric sanity vs your own computed ground truth for a small dataset you construct; provider/model filters actually filter; empty-data state renders cleanly (no NaN/blank charts).
- [ ] **Dashboard:** numbers reconcile with DB counts; charts render without console errors.
- [ ] **Audit page:** actions taken during testing appear; filtering by entity/user/date works; non-admin access behaves per role model.
- [ ] **Users page (admin):** create user, change role, PUT-only editing works; weak/duplicate emails rejected; NO delete expected (PUT-only by design — confirm error handling if delete attempted).
- [ ] **Settings page:** every toggle/field persists after a full reload; invalid values rejected.

## PHASE 4 — Upload & grading pipeline deep-dive (the heart)

**Upload matrix — attempt ALL of these through the UI (Playwright) and confirm server-side enforcement independently via curl:**

- [ ] Valid types: PNG, JPG, PDF, XLSX (plus any other types `attachments.py` accepts — derive the accepted set from code first).
- [ ] Extension lies: `.exe` renamed to `.png`; `.png` renamed to `.pdf`. Must be rejected by content/type validation, not just extension.
- [ ] Zero-byte file; truncated/corrupt header file (valid extension, garbage bytes).
- [ ] Oversized file (e.g. 30 MB image or whatever limit `FileUploadValidator.cs` declares — test just under AND just over the limit; confirm the declared limit actually works).
- [ ] Hostile filenames: `../../evil.png` (path traversal), `answer؟.png` (Persian/RTL characters), spaces + unicode, extremely long names. Storage keys must remain safe GUIDs.
- [ ] Concurrent uploads of two files for the same student+question (uniqueness race).
- [ ] Cancel/abort mid-upload; upload with the backend briefly stopped (error surfaced gracefully, retry works after recovery).

**Rubric ↔ run lifecycle:**

- [ ] Grade with rubric v1 → create v2 with different criteria → grade again → run #1's stored criterion scores still reference v1 semantics; run #2 uses v2. Old runs must be untouched.
- [ ] Attempt to grade a question whose rubric has no active version → controlled error (this class of bug — NO_ACTIVE_RUBRIC 404 — was fixed on 2026-08-25; verify it stayed fixed).

**Grading runs (budget-aware):**

- [ ] First, check whether the grading service supports a stub/mock provider (inspect `config.py`, grader factory, env vars). If not, ADD one behind an env var like `GRADING_PROVIDER=stub` returning deterministic valid JSON with realistic latency (~1–3 s). This is legitimate test infrastructure — keep it, document it in PROJECT_MAP.md, wire it only via configuration.
- [ ] With the stub: run single-question and batch/exam-wide runs; verify per-criterion scores persist, token counts/latency/cost fields populate, raw response stored, validation status correct; run twice → two distinct immutable runs.
- [ ] Malformed-AI-response drill: configure the stub (or temporarily point at an endpoint returning garbage JSON / a 500) → the run must FAIL GRACEFULLY: clear error state, no partial corrupt rows, retry possible.
- [ ] Timeout behavior: stub sleeps longer than the client timeout (~110 s attempt window) → handled without hanging the UI forever or crashing workers.
- [ ] **Exactly ONE live end-to-end call** with a real provider on one question (well inside budget): confirm the full loop including cost estimation realism.
- [ ] **Blind-grading proof:** enter a teacher score for the answer BEFORE running. Capture the exact payload the .NET API sends to the Python service (container logs / debug logging / packet-level via `docker exec` if needed) and assert NO teacher-score field or value is present. Also snapshot-check the DB: `TeacherScoreSnapshot` equals the pre-existing teacher score and was written only after the AI result. This is THE invariant — document the evidence.

**Reviews & metrics closure:**

- [ ] Accept some runs, override others; confirm append-only review history in DB.
- [ ] Recompute evaluation metrics; hand-verify MAE and exact-match % on ≥5 runs against a Python calculation from raw DB values. Check QWK/Pearson on enough data points to be meaningful (fabricate a controlled dataset if needed).

## PHASE 5 — Cross-cutting quality

- [ ] **i18n:** switch to فارسی (FA). Every page you visited: translated labels (no English leakage), correct RTL layout, translated ERROR messages (trigger upload rejection + form validation errors in FA). Switch back — no state corruption.
- [ ] **Persistence:** create an exam/question/rubric → hard reload (F5) → everything still there and correctly rendered; deep-link straight to an entity URL while logged out → redirected to login, then returns to the target page after login if implemented.
- [ ] **Race conditions:** double-submit exam creation; double-click grade-run; two tabs open editing the same rubric.
- [ ] **Backend-down resilience:** stop the API container → UI shows graceful errors everywhere (no white screen); restart → recovers.
- [ ] **Visual/console sweep:** one final pass over every page in both languages collecting console warnings/errors and obvious layout breakage.

---

## Bug ledger & severity scheme

Maintain a live ledger (markdown table) with: ID · Severity · Component · Title · Repro steps · Evidence link · Status (OPEN/FIXED/VERIFIED) · Root cause · Fix commit.

- **S1 Critical:** crash/data loss, security hole (auth bypass, injection, traversal, secret leak), invariant violation (esp. blind grading).
- **S2 Major:** a feature unusable with no workaround; wrong persisted data; misleading metrics.
- **S3 Minor:** feature works but with workaround; poor error messages; wrong status codes; cosmetic-but-functional.
- **S4 Polish:** typos, spacing, console warnings, UX friction.

## Fix protocol (for every bug)

1. Reproduce → capture evidence.
2. Diagnose the ROOT cause (read the actual code path end-to-end; name the file/line).
3. Minimal, correct fix — respect the invariants section; remember the three-layer upload sync rule and i18n strings when relevant.
4. Re-run the EXACT original repro → must pass.
5. Check the fix didn't break neighbors (run adjacent journeys once more).
6. Commit with a message naming the bug ID. Update the ledger.

## Final deliverables

1. **Clean working tree** — all fixes committed logically; `qa-artifacts/` gitignored; no secrets anywhere in the diff.
2. **Regression pass:** re-run the repro of EVERY fixed bug in sequence — all green.
3. **`QA_REPORT.md`** in repo root (commit it):
   - Executive summary: N bugs found (by severity), M fixed & verified, K open with reasons.
   - The complete bug ledger.
   - Coverage matrix: page/journey × tested × result (EN + FA).
   - Pipeline evidence summary: upload matrix results, blind-grading proof (with evidence pointer), live-call count and observed vs estimated cost.
   - Data integrity statement: before/after DB counts, confirmation that runs/reviews/audit remained immutable during the whole session.
   - Recommendations: top 5 robustness improvements you'd make next (don't implement them).
4. Update `PROJECT_MAP.md` for any behavioral changes (including the stub provider, if added).

## Definition of done

Every phase checked off, every confirmed bug FIXED-and-VERIFIED or explicitly justified as open, regression pass green, report committed, and a final checkpoint posted summarizing the session for the user.
