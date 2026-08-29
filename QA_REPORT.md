# QA_REPORT.md — EconGrader E2E QA Session (2026-08-29)

**Scope:** Full-stack E2E QA of EconGrader — React/Vite frontend, .NET 9 API,
Python FastAPI grading service, SQL Server (docker-compose). 8 phases: bring-up,
feature recon, API contract, UI E2E, upload/grading deep-dive, cross-cutting,
fix protocol, regression + report.

**Live AI budget:** 0/10 Claude calls used — grading exercised via the
deterministic **stub** provider (`MODEL_PROVIDER=stub`, see commit `b59d5956`).
No live LLM calls were made during this session.

**Invariants re-verified (unchanged, still holding):**
- Blind grading: teacher score is never in `/grade` payload; snapshot written
  only after AI result; no `teacher` field on the grading DTO.
- Immutable `GradingRun` rows; run/review immutability → `405` on PUT/PATCH/DELETE.
- Exactly one active rubric per question (supersede, never delete).
- Browser talks only to `.NET` via `/api` proxy; grading service is internal.
- No secrets committed this session (see "Secrets note" below).

---

## 1. Environment & Bring-up
- Containers: `api`, `db` (MSSQL 2022), `grading` (stub), `frontend`, `proxy`,
  plus observability stack (dozzle, filebrowser, langfuse).
- API built from current source and recreated; `/api/health` →
  `{status:"ok", dependencies.gradingService.up:true}`.
- Frontend served via Vite (port 5173); SPA deep-links proxied to `/api`.

## 2. Feature Surface Recon (from code)
Mapped all controllers, services, DTOs, role filters (`[Authorize(Roles=…)]`),
file allow-lists, and the grading orchestration path. Confirmed the blind-
grading boundary and audit/immutability model from source.

## 3. API Contract Testing
- All 12 probed endpoints return **401** unauthenticated; tampered / `alg=none`
  / garbage JWTs all **401**. Legacy `X-User-Id` header ignored.
- Role scoping verified: Teacher→Admin endpoints (users, audit) → **403**;
  IDOR attempt (second teacher on owner's exam/question/answer/image/runs/grade)
  → **403**; corrector-attach on another teacher's answer → **403**.
- Run/review immutability: PUT/PATCH/DELETE on run & review history → **405**.
- Answer re-upload replaces by design (**201**), runs preserved; exactly one
  row per (student,question).
- Grading-run count validation solid (0 / -1 / 11 / "two" → **400**);
  unknown answer → **404**.
- Duplicate student `ExternalId` → friendly **409**.

## 4. UI End-to-End (browser MCP)
- Login → Dashboard → Exams → Questions → Rubric → Student answers → Grading
  workspace flows exercised. Teacher-score entry, AI-run trigger, accept/override
  all functional.
- Answer re-upload replaces image; exactly-one-per-pair invariant surfaced as a
  friendly state, not an error.

## 5. Upload & Grading Pipeline Deep-Dive
- File allow-list (png/jpg/jpeg/pdf/docx/xlsx/xls, ≤20 MB) enforced at both
  `.NET` (`FileUploadValidator`) and Python (`attachments.py`); extension lies
  and unknown types rejected (`.exe`→`UNSUPPORTED_MEDIA_TYPE`).
- Stub provider returns deterministic 70%-per-criterion scores; `criteria_scores`
  correctly keyed (`id`) so Python validation passes.
- Upload matrix (valid types, zero-byte, truncated, oversized, unicode filenames)
  handled; concurrency safe.
- Blind-grading proven: `/grade` request body contains no teacher score; snapshot
  persisted only after AI returns.
- Rubric v1/v2 immutability: new version supersedes, prior versions preserved.

## 6. Cross-Cutting Quality
- **i18n (EN↔FA):** language toggle persists across reloads; `dir` switches
  `ltr`↔`rtl` and `lang` switches `en-US`↔`fa-IR`. **Footer now localized**
  (commit `b59d5956` adds `app.footer` key + `t("app.footer")` in `AppLayout`).
- **Deep-link while logged out:** `#/exams` (no token) redirects to login,
  preserving the chosen locale.
- **Console:** no app-level errors; only Vite/React-Router dev-future-flag
  warnings (benign, v7 opt-in).
- **Server-validation error i18n (known limitation):** `friendlyError()` in
  `client.ts` translates only `NETWORK_ERROR`; raw backend error strings
  (e.g. `DUPLICATE_QUESTION_NUMBER`) pass through in English. Documented as a
  non-blocking enhancement — ad-hoc API→i18n maps exist only on LoginPage/Uses.

## 7. Bugs Found & Fixed

| ID | Sev | Component | Symptom | Root cause | Fix | Commit |
|----|-----|-----------|---------|------------|-----|--------|
| BUG-001 | S2 | api/questions | Duplicate question number → **500** | No pre-check; `DbUpdateException` from unique (ExamId,Number) index surfaces as generic 500 | `QuestionService.CreateAsync` pre-checks duplicate → `BusinessRuleException(DUPLICATE_QUESTION_NUMBER)` → **409** | `5aac63a9` |
| BUG-002 | S2 | api/questions rubric | Empty criteria → active rubric with totalMaxScore=0 (poisons grading) | No min-item validation on `CreateRubricAsync` | Reject empty criteria → `BusinessRuleException(EMPTY_CRITERIA)` → **400** | `5aac63a9` |
| BUG-003 | S3 | api/answers | Teacher score > question MaxScore (e.g. 9999 on /20) persisted | Only lower bound (`<0`) validated; upper bound vs `Question.MaxScore` missing | `AnswerService.SetTeacherScoreAsync` loads `Question` (Include) and bounds score → `BusinessRuleException(SCORE_EXCEEDS_MAX)` | `5aac63a9` |

All three surface via the existing `DomainException` middleware as client-safe
errors (stable `code` + message). **`.NET` solution builds with 0 warnings /
0 errors.**

Repro scripts: `qa-artifacts/repro_bug001.sh` (BUG-001).

## 8. Regression Pass
- Rebuilt API image from fixed source; container recreated; `/api/health` green.
- Build is clean (commit `5aac63a9` verified `dotnet build` → 0W/0E).
- No regressions in auth/role/immutability surfaces (re-checked against the
  same endpoints as phase 3).

## Secrets note (out-of-band, not introduced this session)
`qa-artifacts/*_login.json` (JWTs) are **already committed in HEAD** from a
prior commit. They are bearer tokens and should be purged from git history
and rotated. `.gitignore` (restored this session to its `fddcad3d` content)
ignores `/qa-artifacts/` for future work, but the already-committed tokens
remain in history. Recommend `git filter-repo` + token rotation.

## Commits produced this session
- `b59d5956` feat: add stub MODEL_PROVIDER for QA / CI (no live LLM calls)
- `5aac63a9` fix(Bug-001, Bug-002, Bug-003): API contract validations
- `.gitignore` restored (was missing from working tree).

## Outstanding (non-blockers, documented for follow-up)
1. Server-side validation messages not translated in UI (EN-only leak on
   backend errors) — enhance `friendlyError` with a code→i18n map.
2. Committed JWT fixtures should be scrubbed from git history + rotated.
3. `docker-compose.override.yml` is git-ignored locally; the `MODEL_PROVIDER=stub`
   line there drives the QA grading container — keep it for local QA only.
