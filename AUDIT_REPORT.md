# Production Frontend Audit — EconGrader (نمره‌یار)

**Date:** 2026-08-24 · **Branch:** `Frontend/Parsa` · **Auditor:** ox-alpha

## Overall Status

**Needs Fixes → Fixed in this pass.** The app is functionally complete for the core
grading loop and now production-viable after the fixes below. Remaining items are
documented honestly at the end.

---

## Verification Gates (run during this audit)

| Gate | Result |
|---|---|
| `npm test` (Vitest) | ✅ 20/20 passing |
| `npm run lint` (ESLint 9) | ✅ 0 errors |
| `npm run build` (`tsc -b && vite build`) | ✅ 2353 modules |
| i18n forward-check (`check-i18n.mjs`) | ✅ 184/184 keys used exist in en+fa |
| Reverse i18n audit (`check-i18n-unused.mjs`, new) | ⚠️ found 212 unused keys → missing features (fixed subset) |

## Critical Issues

### C1 — Review history never displayed
- **Where:** `WorkspacePage.tsx` / `api/grading.ts`
- **Why it matters:** Accept/Override writes an append-only review record; teachers
  could not see past decisions, undermining trust + auditability.
- **Reproduce:** open a reviewed answer → no history anywhere.
- **Fix:** added `listReviewsForAnswer()` (joins per-run history endpoints) and a
  "Review history" card showing action badge, old→new score, note, relative time.
- **Status:** ✅ FIXED

### C2 — Raw AI response & token usage hidden
- **Where:** `WorkspacePage.tsx`
- **Why it matters:** cost transparency + debugging require raw response and
  input/output token counts; backend stores them but UI omitted them.
- **Fix:** collapsible "Show raw response" panel + token/cost/timestamp strip.
- **Status:** ✅ FIXED

### C3 — No prev/next answer navigation
- **Where:** `WorkspacePage.tsx`
- **Why it matters:** grading hundreds of answers one-by-one is the core workflow;
  users had to return to the queue between every answer.
- **Fix:** footer Prev / Queue / Next navigation over sibling answers of the same
  question, ordered by student ID; buttons disable at list ends.
- **Status:** ✅ FIXED

## High Priority Issues

### H1 — Hardcoded question-image path in AI request (backend)
- Found during API-contract verification: `GradingOrchestrationService` built
  `questions/{examId}/q{n}.png` which never matched real storage keys → uploaded
  question files were silently never sent to the AI.
- **Fixed earlier today** (commit `031db36`): orchestrator attaches every stored
  file that exists.

### H2 — Storage permission failure reported as 403 Forbidden
- Non-root container user vs root-owned volume → `UnauthorizedAccessException`
  mapped to 403, misleading operators into "authorization bug" hunts.
- **Fixed** (commit `f588bd3`): volume ownership pre-seeded in images; middleware
  maps FS failures to 503 STORAGE_*; correlation IDs end-to-end.

## Medium Priority Issues

### M1 — Dead locale keys (212) revealed unwired features
Reverse-i18n audit exposed planned-but-missing UI: rubric versioning controls
(create-new-version/reorder/activate), image viewer zoom/rotate/fullscreen,
keyboard shortcuts, evaluation filters (provider/model), CSV export, queue
prev/next columns. **Partially fixed** (review history, raw response, prev/next).
Remaining keys retained intentionally as roadmap markers; they add ~4 KB gzipped.

### M2 — Answer upload replaced-by-error
Uploading a second answer for the same (student, question) hit the unique index →
500. **Fixed earlier** (`031db36`): upload now replaces the previous file atomically.

### M3 — Evaluation page lacks provider/model filters
Backend supports `?provider=&modelName=` on `/evaluation/question`; UI omits them.
Keys exist (`evaluation.providerFilter/modelFilter`). **NOT FIXED — roadmap.**

## Low Priority / Polish
- `viewer.*` keys unused: workspace shows a plain `<img>`; zoom/rotate/fullscreen
  viewer is future work. PDF answers render via browser viewer when opened directly;
  inline embed in workspace is roadmap.
- `shortcuts.*` keys unused: no keyboard-shortcut help overlay yet.
- `common.exportCsv` unused: no export feature yet.

## API Integration Audit (frontend ↔ backend)

| Call | Verdict |
|---|---|
| GET /api/exams, POST/PUT/DELETE /api/exams | PASS |
| GET /api/questions/by-exam/{id}, GET/POST/PUT/DELETE /api/questions | PASS |
| GET /api/questions/{id}/rubric, POST …/rubrics | PASS |
| POST/GET/DELETE /api/questions/{id}/file, …/rubric/file | PASS (new) |
| GET /api/students, POST /api/students | PASS |
| GET /api/answers/by-question/{id}, GET /{id} | PASS |
| POST /api/answers/upload (PNG/JPG/PDF/DOCX) | PASS (extended) |
| GET /api/answers/{id}/image | PASS (ContentType-aware) |
| PUT /api/answers/{id}/teacher-score | PASS |
| POST /api/grading/run (blind — verified no teacher fields) | PASS |
| GET /api/grading/answer/{id}, /run/{id}, /prompts | PASS |
| POST /api/grading/{runId}/review/accept·override, GET history | PASS (history now wired) |
| GET /api/evaluation/question·exam | PASS |
| GET /api/audit | PASS |
| GET /api/health | PASS |

## Blind Grading Audit
Verified across frontend → API client → controller → orchestrator → Python payload:
teacher score appears **only** in `TeacherScoreSnapshot` persisted *after* the run,
never in any request body. **PASS**

## RTL/Farsi Audit
- Direction flips via `applyDirection` on language change; sidebar/header use
  logical properties (`border-e`, `ms-56`, `rtl:` variants). PASS
- Mixed text uses `.ltr-token` for GUIDs/models/numbers. PASS
- Persian digits via `Intl.NumberFormat` + `toFaDigits`. PASS
- New components (FileAttachment, review history, nav footer) use logical
  utilities only. PASS

## Security Audit
- No secrets/API keys in bundle (grep clean); identity is a user-supplied GUID
  stored in localStorage by design (attribution-only, documented).
- Uploads validated server-side (MIME allow-list, 20 MB cap, sanitized names);
  storage keys GUID-based → no path traversal. PASS

## Performance Audit
- React Query caching with staleTime; manualChunks keep vendor bundles split
  (largest: charts 394 kB / 108 kB gzip). New sibling-answers query reuses the
  same cache key as QueuePage → no duplicate fetches. PASS

## NOT TESTED — ENVIRONMENT LIMITATION
- Live browser click-through of the full grading flow (requires running Docker
  stack + AI key; verified via HTTP probes instead where possible).
- Real AI provider responses (costs money).

## Final Recommendation
With C1–C3, H1–H2 fixed and gates green, I would consider this **ready for a
pilot with real teachers**, with the M-level items (evaluation filters, image
viewer enhancements, DOCX extraction in the Python service) as fast-follows
before general availability.