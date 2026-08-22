# EconGrader — Complete Project Map & Handoff Document

> **Purpose of this file:** a self-contained map of the entire backend so any developer or AI assistant can pick the project up cold — especially for building the frontend UI — without re-reading the whole codebase.
>
> **Status:** Backend COMPLETE and verified. .NET build: 0 errors / 0 warnings. Python tests: 15/15 passing. EF migration `InitialCreate` generated. Docker packaging done. **Frontend: NOT built yet** (this is the next task).

---

## 1. What This App Is

**EconGrader** is a research-grade platform for AI-assisted grading of handwritten economics exam answers.

**Core workflow:**
1. A teacher creates an **Exam**, adds **Questions**, and defines versioned **Rubrics** (per-criterion scoring).
2. Scanned answer sheets (PNG/JPG) are uploaded per student/question as **Answers**. The teacher may store their own ground-truth score (`TeacherScore`) and optionally a second rater's score (`Teacher2Score`).
3. The teacher triggers an AI **Grading Run**. The .NET API sends the question text, rubric criteria, and answer image paths to an internal **Python FastAPI microservice**, which calls an LLM provider (Claude / Gemini / self-hosted Qwen), validates the JSON response, estimates cost, and returns structured scores.
4. The .NET API persists every run with full provenance: provider, model name/version, prompt version, temperature, raw AI response, per-criterion scores, token counts, latency, estimated cost, validation status.
5. Teachers **review** each run: *accept* the AI score or *override* it. Reviews are **append-only** (history is never mutated).
6. **Evaluation metrics** (MAE, RMSE, exact-match %, within-0.5 %, within-1 %, bias, Pearson r, QWK) measure AI-vs-teacher agreement, per question and per exam, filterable by provider/model.

### THE Critical Design Invariant (do not break when building UI)

> **Blind grading:** the teacher's score is **NEVER** sent to the AI service. It is snapshotted onto the `GradingRun` row only AFTER the AI returns its score (`TeacherScoreSnapshot`). The Python service's `/grade` contract has no field for teacher scores at all. Any UI must preserve this guarantee.

Other invariants:
- `GradingRun` rows are immutable evidence records — never edited after creation.
- `TeacherReview` rows are append-only: accepting/overriding creates a new row; history is kept forever.
- `AuditLog` is append-only, queryable by entity/user/date.
- One rubric is "active" per question; new rubric versions supersede but don't delete old ones.
- `Answer` uniqueness: one answer per (student, question).

---

## 2. Architecture

```
┌────────────────────────┐        ┌─────────────────────────────┐
│   FRONTEND (TODO)      │  HTTPS │                             │
│  React/Vue/etc.        │───────▶│   EconGrader.Web (.NET 9)   │
└────────────────────────┘  JSON  │   REST API + orchestration  │
                                  └──────┬───────────┬──────────┘
                                         │           │
                              EF Core    │           │ HTTP POST /grade
                              SQL Server │           ▼
                                         │    ┌──────────────────────────┐
                              ┌──────────▼─┐  │ grading-service (FastAPI)│
                              │ SQL Server │  │ port 5001 (internal only)│
                              │  port 1433 │  │  ├─ Claude  (Anthropic)  │
                              └────────────┘  │  ├─ Gemini  (Google)     │
                                              │  └─ Qwen    (self-hosted)│
                              ┌────────────┐  └──────────┬───────────────┘
                              │ Local disk │             │ SDK calls
                              │ storage/   │             ▼
                              │ images     │      Anthropic/Google APIs
                              └────────────┘
```

- **EconGrader.Web** owns ALL persistence, business rules, audit, and auth-ish headers. The browser must ONLY ever talk to this.
- **grading-service** is internal-only (never expose publicly). It does LLM calls, JSON validation, prompt templates, cost estimation, PDF→image rendering helpers.
- Images are stored on disk via `IFileStorage` (abstraction ready for S3 later); the DB stores relative keys.

---

## 3. Repository Layout

```
econ_grader/
├── EconGrader.sln                      # .NET solution (4 projects)
├── docker-compose.yml                  # db + api + grading stack
├── Dockerfile.api                      # .NET 9 image (non-root, :8080)
├── Dockerfile.grading                  # python:3.12-slim + poppler (:5001)
│
├── src/
│   ├── EconGrader.Domain/              # Entities only, no dependencies
│   │   └── Entities/
│   │       ├── User.cs                 # Email unique; Role enum
│   │       ├── Exam.cs                 # Name, Year, Description, CreatedByUser
│   │       ├── Question.cs             # Number, Text, MaxScore; unique (ExamId,Number)
│   │       ├── Rubric.cs               # Versioned; IsActive; TotalMaxScore computed
│   │       │                           #   + RubricCriterion (CriterionId "1a", MaxScore, Order)
│   │       ├── Student.cs              # ExternalId unique ("S001"), DisplayName
│   │       ├── Answer.cs               # ImageStorageKey, TeacherScore*; unique (StudentId,QuestionId)
│   │       ├── GradingRun.cs           # THE core entity (see §5) + TeacherReview + ReviewAction enum
│   │       ├── AuditLog.cs             # Timestamp, Action, EntityType/Id, UserId, Details, IpAddress
│   │       └── ModelConfig.cs          # Provider+ModelName unique registry
│   │
│   ├── EconGrader.Application/         # Interfaces, services, DTOs, EF DbContext
│   │   ├── Data/AppDbContext.cs        # implements IAppDbContext; all FK/index config
│   │   ├── Interfaces/                 # IAppDbContext, IExamService, IQuestionService,
│   │   │                               # IAnswerService, IGradingOrchestrationService,
│   │   │                               # ITeacherReviewService, IGradingClient,
│   │   │                               # IFileStorage, IAuditLogger (in IFileStorage.cs)
│   │   ├── Services/
│   │   │   ├── ExamService / QuestionService / AnswerService
│   │   │   ├── GradingOrchestrationService.cs    # ← blind-grading snapshot happens here
│   │   │   └── TeacherReviewService.cs           # ← append-only accept/override logic
│   │   ├── Evaluation/EvaluationService.cs       # MAE/RMSE/QWK/Pearson/bias metrics
│   │   └── DTOs/                                 # ExamDtos, QuestionDtos, StudentDtos, GradingDtos
│   │
│   ├── EconGrader.Infrastructure/      # Concrete implementations
│   │   ├── Services/GradingClient.cs   # HttpClient → Python /grade, /prompts
│   │   ├── Services/AuditLogger.cs     # writes AuditLog rows (+Query)
│   │   └── Storage/LocalFileStorage.cs # disk-backed IFileStorage
│   │
│   └── EconGrader.Web/                 # ASP.NET Core host
│       ├── Program.cs                  # Serilog, DI wiring, SQL Server, auto-migrate
│       ├── appsettings.json            # conn string, GradingService:BaseUrl, Serilog
│       ├── Migrations/                 # InitialCreate (20260822...)
│       └── Controllers/                # 9 controllers — see §4
│
└── grading-service/                    # Python FastAPI microservice
    ├── main.py                         # thin re-export of app.main:app
    ├── pricing.json                    # per-token/per-image cost table
    ├── requirements.txt                # fastapi, anthropic, google-genai, pdf2image…
    ├── .env.example                    # keys/config template
    ├── app/
    │   ├── main.py                     # routes: /grade /evaluate /health /prompts
    │   ├── schemas.py                  # pydantic GradeRequest/GradeResponse (THE contract)
    │   ├── config.py                   # env-driven Settings
    │   ├── graders/                    # base + claude/gemini/qwen grader + factory
    │   ├── prompts/                    # default.txt template + loader.py (versioned)
    │   ├── validation.py               # parse_json_safe + validate_grading_response
    │   ├── cost.py                     # estimate_cost from pricing.json
    │   ├── evaluation.py               # compute_metrics + aggregate_by_provider
    │   └── pdf_render.py               # pdf2image helpers
    └── tests/test_validation.py        # 15 passing tests
```

---

## 4. REST API Reference (.NET API — the ONLY thing a frontend talks to)

Base URL: `http://localhost:8080` (Docker) or Kestrel port in dev (`launchSettings.json`).
All JSON responses are **camelCase**. IDs are GUIDs.
Identity: optional `X-User-Id: <guid>` request header = acting user (attribution only; no real auth yet).
OpenAPI JSON available at `/openapi/v1.json` in Development.

### Health
| Method | Route | Response |
|---|---|---|
| GET | `/api/health` | `{status, service, timestamp, dependencies.gradingService:{url, up}}` |

### Exams — `/api/exams`
| Method | Route | Body / Notes |
|---|---|---|
| GET | `/` | → `ExamDto[]` |
| GET | `/{id}` | → `ExamDto` or 404 |
| POST | `/` | body `CreateExamRequest{name, year, description?}` + `X-User-Id` header → 201 `ExamDto` |
| PUT | `/{id}` | body `UpdateExamRequest{name, year, description?}` → `ExamDto` |
| DELETE | `/{id}` | → 204 |

```jsonc // ExamDto
{ "id":"guid", "name":"Microeconomics Final", "year":2026,
  "description":"...", "createdAt":"...", "createdByName":"Dr. X" }
```

### Questions & Rubrics — `/api/questions`
| Method | Route | Body / Notes |
|---|---|---|
| GET | `/{id}` | → `QuestionDto{id, examId, number, text, maxScore, rubricText?}` |
| GET | `/by-exam/{examId}` | → `QuestionDto[]` |
| POST | `/` | `CreateQuestionRequest{examId, number, text, maxScore, rubricText?}` → 201 |
| PUT | `/{id}` | `{text?, maxScore?, rubricText?}` |
| DELETE | `/{id}` | → 204 |
| GET | `/{id}/rubric` | active `RubricDto` |
| POST | `/{id}/rubrics` | `CreateRubricRequest{questionId, criteria:[{criterionId, description, maxScore}]}` + header → creates NEW active version → 201 |

```jsonc // RubricDto — criteria drive per-criterion AI scoring
{ "id":"guid", "questionId":"guid", "version":2, "isActive":true,
  "totalMaxScore":10,
  "criteria":[ {"criterionId":"1a","description":"Correct demand shift","maxScore":3,"order":0} ] }
```

### Students — `/api/students`
| Method | Route | Notes |
|---|---|---|
| GET | `/` | list |
| GET | `/{id}` | single |
| POST | `/` | `CreateStudentRequest{externalId, displayName?}` → 201; **409 Conflict** if ExternalId taken |

### Answers — `/api/answers`
| Method | Route | Notes |
|---|---|---|
| GET | `/{id}` | → `AnswerDto` incl. embedded `gradingRuns[]` summaries |
| GET | `/by-question/{questionId}` | all answers for one question |
| POST | `/upload` | **multipart/form-data**: `studentId`, `questionId`, optional `teacherScore`, `teacher2Score`, binary field `file` (.png/.jpg/.jpeg ≤20MB) → 201 AnswerDto |
| GET | `/{id}/image` | streams scan (image/png|jpeg) — use directly as `<img src>` |
| PUT | `/{id}/teacher-score` | `{score, teacher2Score?}` ground-truth set/update |

```jsonc // AnswerDto
{ "id":"guid", "studentId":"guid", "studentExternalId":"S001", "questionId":"guid",
  "imageStorageKey":"answers/….png", "teacherScore":7.5, "teacher2Score":null,
  "uploadedAt":"...",
  "gradingRuns":[ {"id":"guid","provider":"Claude","modelName":"claude-3-5-sonnet-20241022",
    "promptVersion":"default","temperature":0,"aiScore":8,
    "teacherScoreSnapshot":null,"isValid":true,"error":null,"createdAt":"..."} ] }
```

### Grading — `/api/grading`
| Method | Route | Notes |
|---|---|---|
| POST | `/run` | Kick off AI grading. Body `{answerId, temperature=0, promptVersion="default", provider?, runs=1..10}` → `{runs:[GradingRun], totalRuns, validRuns, medianAiScore}`. 400 bad count · 404 unknown answer · **502** Python service down |
| GET | `/answer/{answerId}` | all runs for an answer (chronological) |
| GET | `/run/{runId}` | full GradingRun incl. rawAiResponse + criteriaScoresJson |
| GET | `/prompts` | `{prompts:["default",…]}` from Python service |

```jsonc // GradingRun
{ "id":"guid","answerId":"guid","questionId":"guid","studentId":"guid",
  "provider":"claude","modelName":"claude-3-5-sonnet-20241022","modelVersion":null,
  "temperature":0,"promptVersion":"default",
  "aiScore":8,
  "teacherScoreSnapshot":7.5,                 // copied AFTER the run — never before
  "rawAiResponse":"{...full model JSON...}",
  "isValid":true,"validationErrorsJson":null,
  "criteriaScoresJson":"[{\"criterionId\":\"1a\",\"score\":3,\"maxScore\":3,\"comment\":\"...\"}]",
  "reasoning":"…","latencyMs":4200,"inputTokens":1500,"outputTokens":320,
  "estimatedCost":0.0091,"error":null,"createdAt":"..." }
```

### Teacher Review — `/api/grading/{runId}/review`
| Method | Route | Notes |
|---|---|---|
| POST | `/accept` | body `{note?}` + `X-User-Id` header → review row with NewScore = AI score |
| POST | `/override` | body `{newScore, note?}` + header → review row with teacher's score |
| GET | `/history` | all reviews for this run (chronological) |

Both POSTs are **append-only**: they create a `TeacherReview` row; nothing is ever mutated.

### Evaluation — `/api/evaluation`
| Method | Route | Notes |
|---|---|---|
| GET | `/question/{questionId}?provider=&modelName=` | AI-vs-teacher agreement metrics for one question; optional provider/model filter |
| GET | `/exam/{examId}` | same rolled up across all questions of the exam |

Metrics returned: MAE, RMSE, exactMatch%, withinHalf%, withinOne%, meanBias, pearsonR, QWK (quadratic weighted kappa), nPairs. Only runs having BOTH ai and teacher snapshot scores are counted.

### Audit — `/api/audit`
| Method | Route | Notes |
|---|---|---|
| GET | `/?entityId=&entityType=&userId=&from=&to=&skip=0&take=100` | paged entries `{id,timestamp,action,entityType,entityId,userId,details,ipAddress}` |
| POST | `/` | manual entry write |

---

## 5. Internal Contract: .NET ⇄ Python

`POST {GradingService:BaseUrl}/grade` — snake_case JSON:

```jsonc // GradeRequest
{ "student_id":"S001", "question_id":"Q1-guid", "question_text":"Explain…",
  "rubric":{"criteria":[{"id":"1a","description":"…","max_score":3}]},
  "answer_image_paths":["/srv/storage/images/answers/…/page1.png"],   // server-side disk paths!
  "question_image_paths":[],
  "max_score":10, "temperature":0, "prompt_version":"default", "provider":"claude" }
```

```jsonc // GradeResponse
{ "run_id":"…","provider":"claude","model_name":"…","model_version":null,
  "prompt_version":"default","temperature":0,
  "ai_score":8,"reasoning":"…",
  "criteria_scores":[{"criterion_id":"1a","score":3,"max_score":3,"comment":"…"}],
  "confidence":0.9,"flagged_ambiguities":[],
  "is_valid":true,"validation_errors":[],
  "raw_response":"…","input_tokens":1500,"output_tokens":320,
  "latency_ms":4200,"estimated_cost_usd":0.0091,"error":null }
```

Other Python endpoints: `GET /health`, `GET /prompts`, `GET /prompts/{version}`, `POST /evaluate`, `POST /evaluate/by-provider`.
Python tests: `tests/test_validation.py` — 15 passing.

Note on images: the .NET app passes absolute disk paths under the shared storage root (`FileStorage:RootPath` == Python `IMAGE_STORAGE_ROOT`). In Docker this means the two containers must mount the same volume at those paths.

---

## 6. Database Schema (SQL Server, EF Core migration `InitialCreate`)

| Table | Key columns | Notes |
|---|---|---|
| Users | Id PK, Email (unique), DisplayName, Role enum, CreatedAt | seed via API when auth lands |
| Exams | Id PK, Name, Year, Description, CreatedByUserId → Users, CreatedAt | |
| Questions | Id PK, ExamId FK cascade, Number, Text, MaxScore, RubricText?, unique (ExamId, Number) | |
| Rubrics | Id PK, QuestionId FK cascade, Version, IsActive bool, CreatedByUserId?, CreatedAt | TotalMaxScore = Σ criteria; new version flips IsActive |
| RubricCriteria | Id PK, RubricId FK cascade, CriterionId ("1a"), Description, MaxScore, OrderIndex | |
| Students | Id PK, ExternalId unique ("S001"), DisplayName, CreatedAt | |
| Answers | Id PK, StudentId FK, QuestionId FK, ImageStorageKey, TeacherScore?, Teacher2Score?, UploadedAt, **unique (StudentId, QuestionId)** | image binary lives on disk |
| GradingRuns | Id PK, AnswerId FK, Provider, ModelName, ModelVersion?, Temperature, PromptVersion, AiScore?, RawAiResponse?, CriteriaScoresJson?, ReasoningJson?, IsValid, ValidationErrorsJson?, TeacherScoreSnapshot?, LatencyMs, InputTokens, OutputTokens, EstimatedCost?, Error?, CreatedAt | immutable evidence row |
| TeacherReviews | Id PK, GradingRunId FK, ReviewerUserId FK Users, Action enum (Accept/Override), NewScore decimal, Note?, CreatedAt | append-only history |
| AuditLogs | Id PK, Timestamp, Action, EntityType, EntityId, UserId?, Details?, IpAddress? | append-only |
| ModelConfigs | Id PK, Provider+ModelName unique, ModelVersion?, InputCostPer1k, OutputCostPer1k, IsActive | registry (no admin endpoint yet) |

All FKs use `Restrict`/`Cascade` as configured in `AppDbContext.OnModelCreating`; indexes exist on all FK columns + unique constraints listed above.

---

## 7. Configuration & How To Run

### .NET `src/EconGrader.Web/appsettings.json`
- `ConnectionStrings:DefaultConnection` — SQL Server (`Server=localhost,1433;Database=EconGrader;User=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=True` in dev)
- `GradingService:BaseUrl` — `http://localhost:5001` dev / `http://grading:5001` Docker
- `FileStorage:RootPath` — default `storage/images`
- `Serilog` — console + daily rolling file in `logs/`

Program.cs wiring: Serilog bootstrap → controllers + JSON camelCase → EF Core SQL Server (**EnableRetryOnFailure**, migrations assembly = Web) → scoped services (ExamService, QuestionService, AnswerService, GradingOrchestrationService, TeacherReviewService, EvaluationService) → `IFileStorage`=LocalFileStorage, `IAuditLogger`=AuditLogger → typed `HttpClient` resilience pipeline for `IGradingClient` → 50MB multipart limit → auto-migrate on startup.

### Python `grading-service/.env` (template in `.env.example`)
`MODEL_PROVIDER`, `MODEL_NAME`, `ANTHROPIC_API_KEY`, `GOOGLE_API_KEY`, `QWEN_BASE_URL`, `QWEN_API_KEY`, `DEFAULT_TEMPERATURE`, `DEFAULT_MAX_TOKENS`, `IMAGE_STORAGE_ROOT`, `PROMPTS_DIR`, `LOG_LEVEL`.

### Run modes
```bash
# Full stack
docker compose up --build        # db:1433 · grading:5001 · api:8080

# Local dev (two terminals + SQL Server)
dotnet run --project src/EconGrader.Web          # auto-migrates on boot
cd grading-service && uvicorn app.main:app --host 0.0.0.0 --port 5001
```

---

## 8. Known Gaps / Frontend Build Brief

1. **No authentication yet** — identity is a trusted `X-User-Id` header. The `Users` table exists for later real auth.
2. **No frontend** — build a SPA against §4 endpoints. Suggested pages:
   - *Exams* list/detail with question editor + active rubric editor (versioned)
   - *Students* management
   - *Upload* flow (multipart POST `/api/answers/upload`)
   - **Grading workspace** (core screen): answer scan (`/api/answers/{id}/image`) beside latest run result (`/api/grading/run/{id}`), per-criterion breakdown parsed from `criteriaScoresJson`, Accept / Override buttons → review endpoints, run-history timeline, "Run AI grading" button → POST `/run`
   - *Evaluation dashboards* from `/api/evaluation/*`
   - *Audit viewer*
3. **CORS not configured yet** — add `AddCors` for the frontend origin during UI work.
4. Ensemble median returned by POST `/run` is computed in the controller, not persisted.
5. `ModelConfigs` has no admin endpoint yet.

## 9. Verification Commands

```bash
dotnet build                                                          # 0 errors, 0 warnings
cd grading-service && ../.venv/Scripts/python.exe -m pytest tests -q  # 15 passed
dotnet ef migrations list --project src/EconGrader.Web                # InitialCreate present
docker compose up --build                                             # full stack up
```

