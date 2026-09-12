# EconGrader — Production Foundation Plan

*Decisions, target architecture, schema, phased milestones, migration, and risks. Input for a Claude Code implementation session. Companion to `ai-grading-efficiency-analysis.md` (current-state analysis + R1–R4).*

---

## 0. Decisions locked in (with eyes open)

| Decision | Choice | Why | Trade-off accepted |
|---|---|---|---|
| Scale | **Medium: 500–5,000 answers/exam** | Current single-host Docker stack serves this fine | No Redis/RabbitMQ now; DB-backed queue. Scaling path documented if load grows. |
| Answer upload model | **Keep: one file per question, per student** | Matches today's UI and data model; teachers grade question-by-question | Per-student whole-exam batch grading is **not** the core win here — it becomes an optional add-on. Main wins: render-once artifacts, queue parallelism, smaller images. |
| Provider | **Stay on GLM gateway (ArvanCloud, OpenAI-compatible)** | Working today, zero token cost | No prompt-caching / first-party API work in this plan. Abstraction already supports a second slot (GPT) if economics change. |
| Scope | **Full foundation, phased (5 milestones)** | One rebuild instead of patching request-time conversion forever | More upfront work than quick wins; each milestone ships working software, so risk is staged. |

**Explicitly out of scope (documented so nobody asks later):** Redis/RabbitMQ broker, multi-host storage (S3/MinIO), per-student whole-exam batch grading, first-party provider SDKs, self-hosted vLLM serving, prompt-caching integration.

### Verified facts this plan stands on

- Single-host Docker Compose: SQL Server 2022 + FastAPI grading service + .NET API + nginx SPA + Caddy. Shared `app_storage` volume mounted at `/srv/storage` in **both** api and grading containers; grading receives absolute file paths.
- `LocalFileStorage` is key→path under one root, with path-traversal guard — a clean seam for content-addressed keys.
- HttpClient to grading service: 330 s timeout (Polly 315/320 s policy). Any queue design must move long work out of this window.
- `ModelConfig` table exists but is unused (pricing comes from `pricing.json`) — cleanup candidate, not a blocker.
- Langfuse tracing is optional and failure-isolated — carries over unchanged.
- Extraction caps exist (`EXTRACT_MAX_PAGES=20`, 150k chars) — reuse for ingest.
- `DEFAULT_MAX_TOKENS=8192` headroom note for thinking models (observed live) — preserve in any grader change.

---

## 1. Target architecture (what the app becomes)

### 1.1 The flow, after

**Exam setup (unchanged UX, new internals):**
Teacher uploads the grading key → ingest renders pages **once** (content-addressed) → existing extraction preview → teacher confirms → questions + rubrics saved, **each with references to its rendered page assets**.

**Answer upload (unchanged UX, new internals):**
Teacher uploads a student's answer file per question → blob stored by content hash → ingest job renders/normalizes pages once (150 DPI JPEG) → answer record points at original + derived pages.

**Grading (the big change):**
Teacher clicks "grade all" on an exam/question (or a single answer) → **jobs are enqueued** → a worker pool with bounded concurrency picks them up → each job: load item + rubric + answer pages (all already on disk, zero conversion) → build messages → one AI call → persist GradingRun (unchanged entity) → progress visible in UI. No HTTP request ever waits on the model.

### 1.2 Components

```
┌─────────┐   ┌──────────┐   ┌─────────────────┐   ┌──────────┐
│ frontend│──▶│  api     │──▶│ GradingJobs (DB)│──▶│ worker   │──▶ GLM gateway
└─────────┘   │ (.NET)   │   │  + IngestJobs   │   │ (.NET,   │
              │ upload/  │   └─────────────────┘   │ N workers│
              │ enqueue  │                         └────┬─────┘
              └────┬─────┘                              │ HTTP /grade (fast, local)
                   ▼                                    ▼
              blobs/ + pages/  ◀───────────────  grading-service (FastAPI)
              (content-addressed, rendered once)      conversion REMOVED from
                                                      hot path (kept for ingest)
```

- **api** (.NET): auth, upload, enqueue jobs, serve UI data. Never blocks on the model.
- **worker** (.NET, new container, same codebase with `AppRole=worker`): leases GradingJobs, bounded concurrency (default 4), calls grading-service, persists runs, updates job status. Scale = `docker compose up --scale worker=N`.
- **grading-service** (Python): keeps `/grade` + `/extract` (contract unchanged), gains `/ingest` (render/normalize once), loses per-request conversion work in practice (kept for backward compat).
- **DB**: SQL Server — `Artifacts`, `QuestionAsset`, `GradingJob`, `IngestJob` tables (details §2).

### 1.3 Content addressing

New blob layout under the existing storage root:

```
blobs/{sha256[0:2]}/{sha256}{ext}          ← immutable originals
pages/{sha256[0:2]}/{sha256}-p{n}.jpg      ← rendered/derived page images
```

`Artifacts` table: one row per blob/page — `id, kind (original|page), storage_key, sha256, bytes, width, height, page_number, parent_artifact_id, created_at`. Records never reference files directly; they reference artifact rows. **Crucial: `Sha256` on a `page` artifact is the hash of the RENDERED page image's own bytes, NOT the parent document's hash** — this is what makes dedup real (the same figure inside two different exam PDFs renders to identical page bytes → one stored image, two artifact rows or one deduped row). The parent hash lives in `parent_artifact_id` lineage, not in the page's identity. The original-blob dedup (identical file uploaded twice) is a separate, simpler win from the same rule applied at the `original` level.

---

## 2. Schema changes (EF Core, one migration per milestone)

**M1 — Artifacts & item banking**
```csharp
class Artifact { Guid Id; string Kind; string StorageKey; string Sha256; long Bytes;
                 int? PageNumber; int? Width; int? Height; Guid? ParentArtifactId; DateTime CreatedAt; }
// unique index (Sha256, Kind, PageNumber)
// Kind=page: Sha256 = hash of the RENDERED page's own bytes (not the parent doc) → real dedup.

class QuestionAsset { Guid Id; Guid QuestionId; Guid ArtifactId; int SortOrder; string Role; } // Role: question_paper|figure
class QuestionAnswerKey { Guid Id; Guid QuestionId; Guid ArtifactId; int SortOrder; string Role; } // Role: model_answer|model_figure

class Answer { ...existing...; Guid? OriginalArtifactId; }  // null until migrated
// Exam gains: bool GroundTruthGradingEnabled = false;   // پاسخنامه gate — ships M1, inert until M4 evidence
// Question/Exam rubric-file columns gain parallel ArtifactIds (nullable, coexist with legacy keys).
```

**M2 — Jobs**
```csharp
class GradingJob { Guid Id; Guid AnswerId; decimal Temperature; string PromptVersion; int EnsembleIndex;
                   string Status;        // pending|running|completed|failed|cancelled
                   int Attempts; string? ErrorKind; string? Error; DateTime? StartedAt; DateTime? FinishedAt;
                   string? LeaseToken; DateTime? LeaseUntil; Guid? GradingRunId; }
// unique index (AnswerId, Temperature, PromptVersion, EnsembleIndex) → idempotency
class IngestJob  { Guid Id; Guid TargetArtifactId; string Kind; string Status; int Attempts; ... } // same lease pattern
```

**M4 — Lineage**
```csharp
class GradingRun { ...existing...; string? InputArtifactsJson; }  // [{kind, sha256, page}]
```

**Not changed:** User/Role model, ExamCorrector scoping, TeacherReview append-only flow, Rubric versioning, audit logging, ModelConfig (marked unused; remove in a later cleanup PR).

---

## 3. Milestones (each ships working software)

### M1 — Ingest pipeline + item banking *(foundation; ~core of the plan)*
- `POST /ingest` in grading-service: file path in → renders PDF→pages (150 DPI JPEG q85, capped like `EXTRACT_MAX_PAGES`), extracts DOCX/XLSX text → artifacts written + registered. Deterministic: same input hash → skips re-render.
- .NET upload endpoints (exam rubric file, question file, answer file) switch to: save blob by hash → enqueue IngestJob → record artifacts. Legacy storage keys keep working side-by-side (nullable FKs).
- Extraction confirm (`ApplyAsync`) also saves `QuestionAsset` rows pointing at the exam key's rendered pages relevant to each question (page numbers from extraction output or teacher's pick in preview UI).
- **پاسخنامه upload (new):** `POST /questions/{id}/answerkey` (teacher role, same validators as question-file upload) → blob by hash → ingest → `QuestionAnswerKey` rows. Same page-picker as question assets: teacher confirms which rendered pages are the model answer for this question. Multi-file keys supported (SortOrder). **Grading integration — gated, inert by default (same milestone):** the worker builds the grading prompt WITH the model-answer block **only when `Exam.GroundTruthGradingEnabled` is on** — the flag ships here in M1 (default off), NOT in M4, so uploading a پاسخنامه never changes live grading behavior until evidence unlocks it. When on: prompt rule block ("the model answer shows the examiner's expected solution — use it as the reference for what a correct answer contains; grade against reference + rubric, not your own knowledge; when the reference does not cover a criterion, fall back to rubric + your knowledge for that criterion only"). پاسخنامه presence is recorded in lineage (`InputArtifactsJson` role=model_answer) and surfaced in the run UI ("graded against پاسخنامه p1–p2"). **DPI/JPEG likewise gated:** ingest renders to BOTH 200 DPI PNG (current default, unchanged grading behavior) and 150 DPI JPEG (new path); which one the grader consumes is a config flag (`Ingest:PageFormat=png200|jpeg150`), so the golden-set harness can A/B the formats before the JPEG path becomes default.
- **Accept:** re-grading the same question twice does zero re-rendering (verify via logs/artifact hits); uploads of identical files dedup to one blob; a grading run with a پاسخنامه provably consumed it (hash in lineage); a run without one behaves exactly as today.
- **Risk:** extraction may not know which pages belong to which question → mitigate: page-range picker in the confirm UI, defaulting to "all exam-key pages" for questions without a pick (correct today, better later).
- **Risk (پاسخنامه):** examiner's key conflicts with the rubric (key shows more/fewer marks than criteria sum) → the prompt instructs per-criterion fallback; validation still enforces the rubric's max_score caps (existing `CRITERIA_SUM_EXCEEDS_MAX` logic unchanged) and a mismatch is surfaced in flagged_ambiguities for teacher review, never silently resolved.

### M2 — Job queue + workers
- `GradingJobs` table + lease-based worker (poll with `READPAST`/rowversion, or `UPDATE TOP(1) … OUTPUT` claim; SQL Server-safe).
- Worker container added to compose (`worker` service, same image as api, `AppRole=worker`).
- **Concurrency is a GLOBAL budget, not per-process:** the lease-claim query respects a max-in-flight cap (a single row in a `Settings`-style table, or `COUNT(*) WHERE Status='running' < N` inside the claim transaction). Scaling `--scale worker=N` adds failover/throughput headroom, but total concurrent model calls stay ≤ the global cap (default 4) — per-process concurrency is deliberately NOT a knob, so "scale workers" never silently multiplies gateway load.
- **Lease renewal:** lease duration set ≥ p99 grading latency ×2 (config, default 10 min); while a job is actively processing the worker heartbeats `LeaseUntil += lease_duration` every ~60 s, so a legitimately slow gateway call can never be double-claimed mid-flight. A genuinely dead worker stops heartbeating → lease expires → re-claim. Idempotency key still guards the completed-row INSERT as the last line of defense.
- `POST /grading/run` becomes: validate + enqueue → 202 with job id(s). Ensemble = N jobs (parallel by construction, but all respecting the global in-flight cap).
- New `GET /grading/jobs?answerId=…` + `?examId=…&status=…` for the UI; QueuePage/WorkspacePage poll (3 s) and render live status. Reuse existing run timeline for results.
- Keep old synchronous path behind a feature flag for one release (`Grading:Mode=sync|queue`).
- **Accept:** 30 answers × 1 run completes in ~⌈30/4⌉ × latency instead of 30 × latency; `--scale worker=2` does NOT double gateway concurrency (verify: never more than 4 simultaneous upstream calls); killing the worker mid-run leaves the job `running` with expired lease → re-claimed and completed exactly once (idempotency key); a heartbeat-holding job is never stolen mid-call.
- **Risk:** double-execution on lease expiry → run INSERT guarded by the unique job key + heartbeat renewal makes expiry-during-flight unlikely by construction.

### M3 — Bulk grading UI
- `POST /grading/bulk {examId, questionId?}` → enqueues one job per answer (teacher role, existing access-scope checks per answer).
- WorkspacePage/QueuePage get "Grade all remaining" (exam or question scope), with live per-answer status chips.
- **پاسخنامه UI:** question editor + workspace question card gain "پاسخنامه" management (upload/replace/remove, page-picker on confirm, multi-file list). The "graded against پاسخنامه p1–p2" chip appears on run rows; bulk grading automatically includes the key when present — no per-answer setup.
- **Accept:** whole-exam grading from one click; failures are per-answer, never blocking the batch; partial results all persisted; a question with a پاسخنامه is visibly marked in question lists.

### M4 — Lineage + golden-set eval harness
- GradingRun records `InputArtifactsJson`; run detail shows "graded against pages p2–p3 (sha256…)".
- `POST /evaluation/golden-set`: pick N teacher-scored answers as the frozen set; on prompt/model change, re-grade them and diff QWK/MAE/exact-agreement (evaluation.py already computes all three) before/after. Store harness results in a simple `EvalRuns` table (or reuse audit log for v1).
- **Note:** the `GroundTruthGradingEnabled` and `Ingest:PageFormat` flags this harness gates already shipped in M1 (inert until now) — M4 is where they get turned on WITH evidence: golden-set A/B of پاسخنامه-on vs پاسخنامه-off grading, and JPEG-150 vs PNG-200, rolling out only on non-regression.
- **Golden-set hygiene:** the set is refreshed/rotated periodically (e.g. fold in newly teacher-confirmed answers quarterly, keep a held-out slice that is NOT used for day-to-day gating) so repeated tuning doesn't silently overfit to one fixed sample ("doesn't regress QWK" must not quietly become "doesn't regress QWK on this one frozen set forever").
- **Accept:** a prompt change that regresses QWK is visibly flagged before rollout; a پاسخنامه-enabled exam that regresses is flagged before exam-wide rollout; the held-out slice stays honest over time.

### M5 — response_format + payload hygiene
- Grader calls add `response_format: {"type":"json_object"}` (schema-strict if the gateway accepts it); hardened parser stays as fallback. Keep the 8192 max_tokens thinking-headroom note.
- Message ordering standardized: system → question text+rubric → question paper pages → student pages **last** (prefix-stable for future cache providers, costs nothing now).
- Ensemble prompt-variance option: optional slight temperature stagger across ensemble indices (default stays 0.0).
- **Accept:** parse-failure retry rate drops (visible in Langfuse/logs); no behavior change when parser succeeds.

### Deliberately deferred
Per-student whole-exam batch grading (upload model is per-question); Redis broker (DB queue is fine at this scale); S3/MinIO (local volume + hash layout ports trivially later); prompt caching (provider-dependent).

---

## 4. Migration of existing data

Backfill script (idempotent, run per milestone):
1. **M1:** for each distinct `FileStorageKey` (exam rubric, question file, answer) — hash the file, create original artifact, enqueue ingest, create page artifacts, backfill nullable FKs. Legacy keys stay untouched → **zero downtime, instant rollback** (flip back to sync mode + legacy reads).
2. **M2:** nothing to migrate (new table). In-flight synchronous runs finish first (flag flip during quiet window).
3. **M4:** backfill `InputArtifactsJson` for new runs only; historical runs keep their existing snapshots (rubric/prompt versions) — acceptable, documented.

---

## 5. Risks & mitigations (wide-open-eyes register)

| Risk | Likelihood | Mitigation |
|---|---|---|
| Worker/api image divergence (same repo, two roles) | Low | One image, `AppRole` env; CI builds once |
| Lease expiry double-run | Medium | Idempotency key + completed-run guard + heartbeat renewal (M2) |
| `--scale worker=N` silently multiplying gateway load | Medium | Global in-flight cap enforced inside the claim query — scaling workers never raises concurrent model calls (M2) |
| 150 DPI JPEG hurting handwriting OCR vs 200 DPI PNG | Medium | Both formats rendered at ingest; `Ingest:PageFormat` flag holds old behavior until golden-set A/B (M4) passes |
| Extraction can't attribute pages to questions | High (first use) | Page-picker in confirm UI; default = all pages (safe fallback) |
| GLM gateway timeout variance under 4× concurrency | Medium | Bounded concurrency config; existing retry taxonomy (parse/timeout) carries over; worker marks job failed-with-errorKind for requeue |
| DB queue contention at burst | Low at this scale | Rowversion/READPAST claim; defer broker until >~50k jobs/day |
| Migration bugs on production files | Medium | Idempotent backfill + legacy-path fallback + per-milestone rollback |
| پاسخنامه changes live grading before evidence exists | — (eliminated) | Flag `GroundTruthGradingEnabled` ships in M1 default-off; grading behavior is inert until the M4 golden-set gate turns it on |
| پاسخنامه conflicts with rubric (marks mismatch) | Medium | Per-criterion fallback prompt rule + rubric max_score caps enforced by validation + mismatch surfaced in flagged_ambiguities |
| پاسخنامه reduces scores across the board (AI grades stricter against reference) | Medium | Golden-set gate before per-exam enable (M4) + per-exam opt-in flag + teacher review of flagged_ambiguities |

## 6. What the app becomes (summary)

Same UI, same roles, same review flow, same grading quality rules — but underneath: **every file rendered once** (never per request), **every grading run a resumable job** (no 5-minute HTTP holds, bulk grading by default, ensemble in parallel), **every run provable** (artifact hashes + rubric/prompt versions = full lineage), and **every prompt/model change gated** by a golden-set eval using the teacher scores you already collect. Questions can carry the examiner's own **پاسخنامه** — the AI grades against the examiner's expected solution as the reference (with per-criterion fallback and rubric caps enforced), so grading is grounded in the examiner's intent, not just the model's knowledge. Token spend drops ~3–5× from image format alone; wall-clock for a 30-student exam drops ~4× from worker parallelism; and the day provider economics change, the item-bank + artifact foundation makes prompt caching / batch APIs a config change, not a rebuild.
