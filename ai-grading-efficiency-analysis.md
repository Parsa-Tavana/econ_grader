# EconGrader — AI Grading Efficiency Analysis

*Expert scan of the grading pipeline, with recommendations for token & time efficiency. Written as groundwork for a Claude Code implementation plan.*

---

## 1. What the app does today (verified from code)

**Stack:** .NET 9 API (`src/`) + Python FastAPI grading-service (`grading-service/`) + React frontend. Providers: OpenAI-compatible gateways (GLM slot default, GPT slot reserved).

**Rubric extraction flow (already good):**
- One exam-wide rubric file (grading key) → `POST /extract` → **one single AI call** returns ALL questions + criteria as JSON → editable preview → confirmed rows upserted to DB.
- This is the "thing I did for rubric extraction" — one call, whole document, structured JSON back. It's the right pattern.

**Grading flow (the inefficiency):**
- Per answer: teacher clicks "run" → .NET `GradingOrchestrationService.GradeAnswerAsync` → `POST /grade` → Python re-converts files → `OpenAICompatibleGrader.grade()` → **one AI call per (student answer × question)**.
- Ensemble mode: `Runs` 1–10 — a **sequential for-loop** in `GradingController.Grade`, each iteration a full separate AI call.

### The cost structure of one grading call

Every single call re-sends:

| Component | Source | Cost profile |
|---|---|---|
| Question paper images | `answer.Question.FileStorageKey` → re-rendered PDF→PNG at 200 DPI, base64-inlined | **Identical for every student & every run** of the same question |
| Question text + rubric JSON | DB, formatted into prompt each time | Identical per question |
| System prompt + grading prompt | `SYSTEM_PROMPT` + `default.txt` | Identical for ALL calls |
| Student answer images | `answer.ImageStorageKey` | The only truly unique part |

So for a class of 30 students × 10 questions, the question papers are re-sent up to 300 times. At 200 DPI PNG, a few question pages can dominate the input token bill — the unique data (student handwriting) can be the minority of what's transmitted.

### Other inefficiencies found

1. **No concurrency anywhere.** Ensemble runs are sequential (`for i in 0..Runs`); no bulk-grading endpoint exists (QueuePage has no runGrading import — grading is strictly one answer at a time, by hand). 30 students × 1 run = 30 sequential round-trips of 30–140 s each.
2. **PDF → PNG at 200 DPI, PNG format.** Base64 inflates 33%. No downscaling, no WebP/JPEG, no page-count cap for answers (only extraction has `EXTRACT_MAX_PAGES`). Large answer PDFs blow both latency and context.
3. **Everything re-converted on every call.** `prepare_attachments` re-reads and re-renders the same question-paper PDF per request; no cache keyed on file.
4. **No `response_format: json_schema`.** The app relies on prompt-instructed JSON + a hardened repair parser + retries. Structured output would cut parse failures (and thus retry calls — a retry re-sends the ENTIRE payload including images).
5. **Retry strategy re-sends everything.** Parse/timeout retries and "image unreadable" retries repeat the full image payload. Correct behavior, but it makes payload size a reliability issue too, not just cost.
6. **Single-image answers only at the .NET layer.** `Answer.ImageStorageKey` is one file; multi-page answer sheets must be one PDF — fine, but no concept of "one student, many questions" as a unit.
7. **Cost estimator reads pricing.json from disk on every call** (minor; all prices currently 0.0 — GLM slot appears free/self-hosted, so today *time* is the dominant cost, but the architecture tax applies the day you pay per token).

---

## 2. Recommendation set — ordered by impact

### R1. Cache the shared per-question "grading context" (biggest structural win)

Split each grading call into **shared context** (question paper images, question text, rubric) and **unique payload** (student answer).

**Option A — server-side materialization (provider-agnostic, works with your gateway):**
- On first grade of a question, render the question PDF once and store the page PNGs (and rendered prompt header) on disk keyed by `questionId + rubric version + file hash`.
- Every subsequent `/grade` call for that question references the cached images instead of re-rendering/re-uploading through the .NET→Python hop.
- ⚠️ Note: if your gateway supports **prompt caching** (OpenAI-style `cache_control` / automatic prefix caching), also order the message so shared content comes FIRST (system prompt → question paper → rubric → student answer last). Prefix-caching then discounts the shared part. If the gateway does not support caching, Option A still saves your own CPU/latency, and this ordering costs nothing.

**Impact:** question-paper render cost paid once per question instead of per call; input payload per call shrinks to student pages + rubric.

### R2. Grade all questions of one student in ONE call (batched grading)

You already proved the pattern with extraction: one call, whole document, structured JSON back. Apply the same idea to grading:

- New endpoint `POST /grade/student`: input = student's answer file(s) for an exam + the list of questions (each with its structured rubric). Output = one JSON with a per-question array of the same `criteria_scores` schema you already validate.
- The exam paper (all questions) is sent **once** instead of 10 times; the student's answer sheet is sent **once** instead of 10 times.
- Extraction currently caps at 20 pages / 150k chars — reuse those caps for grading batches.
- Keep the existing per-question `/grade` endpoint for spot re-grading of a single answer.

**Impact:** for a 10-question exam, ~10× fewer calls, ~10× less repeated question-paper input; also one fewer round of handwriting-reading per call.

Trade-off to manage: longer output (10 question verdicts in one JSON) → need `EXTRACTION_MAX_TOKENS`-style budget and the same staged validation/retry you already have. Do it in chunks if answers are long (e.g. batch questions in groups of 3–5).

### R3. Bulk queue grading with bounded concurrency

There is no "grade the whole queue" path. Add:
- `POST /grading/bulk` accepting examId (+ optional questionId filter), which fans out per-answer grading with a **bounded parallelism** (e.g. `Parallel.ForEachAsync` with 4–8 workers, configurable) instead of the current sequential loop.
- Persist each run as it completes (you already persist per run — keep that), and report progress. A cheap polling endpoint or SSE keeps the UI live.
- Same fix inside ensemble mode: run the N ensemble passes in parallel, not sequentially.

**Impact:** wall-clock time drops from N×latency to N/workers×latency. This is pure orchestration — no model change needed.

### R4. Enforce structured output & shrink images

- Add `response_format: {"type": "json_schema", ...}` (or `json_object` if schema unsupported) to both grade and extract calls. Keep the hardened parser as fallback. Fewer parse failures = fewer full-payload retries.
- In `pdf_render.py`: render answers at ~150 DPI and convert to JPEG (quality ~80) or WebP; cap answer pages sent per call with an explicit warning (mirror `EXTRACT_MAX_PAGES`). Handwriting grades fine at 150 DPI JPEG; token cost per image drops substantially, base64 payload shrinks ~3–5×.

### R5. Smaller protocol wins

- Reuse a prepared-attachments cache (file hash → rendered pages) with an LRU/TTL inside the Python service.
- In ensemble/multi-run mode, reuse the *rendered* attachments object across runs (currently each `GradeAnswerAsync` re-renders from scratch).
- Load `pricing.json` once at startup with a file-watch.
- Consider streaming (`stream: true`) for extraction so a slow gateway doesn't hit the 280 s read timeout on long generations — retries on extraction are the most expensive calls in the system.

---

## 4. Production-grade foundation — what large-scale grading platforms do

The patterns above (R1–R4) are optimizations *inside* the current request-time architecture. The production-grade move is to change the architecture itself. Grading platforms at scale (Gradescope/Turnitin, ETS, Cambridge Assessment, Duolingo) all use the same five patterns:

### 4.1 Item banking (the assessment-industry standard)

Each question is a **versioned item**: text (verbatim), rubric criteria, metadata, and **references to assets** — the page images / figures / graphs it requires. Non-text content is NOT squeezed into markdown/JSON; it lives as binary files on disk, and the item record holds structured references:

```json
{
  "number": 3,
  "text": "…verbatim question text…",
  "rubric": [...],
  "assets": [
    { "kind": "page_image", "storage_key": "exams/<exam>/pages/p02.jpg", "page": 2, "sha256": "…" }
  ]
}
```

Grading consumes items — shared exam material is sent from stored artifacts, never re-derived from the original PDF. This resolves the "question isn't fully text" concern: text → record; figures → files + references; the record is fully JSON-serializable and versioned like the rubric already is.

### 4.2 Ingest-time materialization ("prepare at write, cheap at read")

Netflix doesn't transcode on play; EconGrader shouldn't render on grade. Move ALL conversion to upload time:

```
Upload exam rubric PDF
  → sha256 → store immutable blob
  → render pages once (150 DPI JPEG/WebP) → store page images as derived artifacts
  → AI extraction (existing /extract flow) → question items with asset references
  → teacher confirms → items persisted (versioned)

Upload answer sheet
  → sha256 → immutable blob → render/normalize once → answer artifacts

Grading run
  → read item record + referenced page images (already on disk)
  → read answer artifacts (already on disk)
  → messages: [system → shared exam context first → student answer last]
  → one AI call, zero conversion, zero re-rendering
```

Derived artifacts are registered in DB (`artifacts` table: storage_key, kind, sha256, parent_blob, created_at) so the pipeline is resumable and auditable.

### 4.3 Content-addressed, immutable storage

Store blobs keyed by content hash; never mutate. Consequences: free deduplication (identical figure across exams stored once), built-in integrity checking, and **lineage** — extend the existing GradingRun snapshot (rubric version, prompt version) with the asset hashes consumed, so every run can prove exactly which artifact versions produced its score.

### 4.4 Queue-driven, idempotent grading jobs

Long work never lives inside an HTTP request (today: a 330 s synchronous window per run). Production shape: enqueue → 202 + job id → UI polls/SSE. Jobs are idempotent, keyed on (answer, question, rubric_version, prompt_version) so retries can't double-grade or double-bill. Bulk grading becomes a bounded-concurrency worker pool over the queue: survives restarts, resumes on failure, progress is observable per item. Ensemble runs become N queue jobs instead of a sequential loop.

### 4.5 Golden-set evaluation harness

A frozen set of teacher-graded answers ("golden set") runs against every prompt/model change before it ships; QWK/MAE/exact-agreement (already implemented in evaluation.py) must not regress. This turns prompt iteration from vibes into gated releases — and the ground truth (teacher scores) already exists in the app.

### How R1–R4 fold in

R1 (context cache) is subsumed by 4.2 — question material is materialized once at ingest. R2 (per-student batched grading) becomes a thin endpoint over items + answer artifacts. R3 (bulk + concurrency) is 4.4. R4 (image format + response_format) applies at the materialization step and the grader call respectively.

## 5. Implementation order (for the Claude Code plan)

**Foundation first (section 4 patterns), then layer the wins on top:**

1. **4.2 + 4.3 — ingest pipeline & content-addressed artifacts.** The foundation everything else stands on: materialize once at upload, store derived pages, register artifacts. Includes R4's image format decisions at materialization time.
2. **4.1 — item banking with asset references.** Extend the existing extraction flow to save page-image references with confirmed questions; grading reads items instead of re-deriving from PDF.
3. **4.4 — queue + idempotent jobs + bulk grading.** Replaces the synchronous 330 s window; parallel ensemble and whole-exam grading fall out of this naturally.
4. **4.5 — golden-set eval harness.** Gate all later prompt/model changes with existing metrics before shipping.
5. **R2 — per-student batched grading** on top of items (cheap now: one call, items + answer artifacts already prepared).
6. **R4b — `response_format: json_schema`** on grader calls; keep the hardened parser as fallback.

Measure before/after with the metrics you already record (`InputTokens`, `LatencyMs`, `EstimatedCost` on `GradingRun`) — the dashboard can show the improvement directly.

---

## 4. Things that are already good (keep)

- Extraction = one call for the whole grading key, with edit-before-apply preview. This is exactly the pattern to extend to grading (R2).
- Rubric travels as structured JSON, never as a document at grading time.
- Strict evidence-based prompt with zero-by-default, criterion-id reconciliation, and Persian-language output handling.
- Transient-retry classification (parse/timeout) and teacher-blind request design.
- Full audit trail per run with token/latency/cost snapshots — everything needed to measure the improvements.
