// ── Mirrors of backend DTOs (camelCase JSON, GUID ids) ────────────────────────

export interface ExamDto {
  id: string;
  name: string;
  /** ISO date string (YYYY-MM-DD) — the exam's date, day precision. */
  examDate: string;
  description?: string | null;
  createdAt: string;
  createdByName: string;
  rubricFileName?: string | null;
  rubricFileContentType?: string | null;
  /** پاسخنامه-graded runs enabled for this exam (M1 gate, default off — M4 flips with evidence). */
  groundTruthGradingEnabled?: boolean;
}

export interface CreateExamRequest {
  name: string;
  /** ISO date string (YYYY-MM-DD). */
  examDate: string;
  description?: string | null;
}

export interface UpdateExamRequest {
  name: string;
  /** ISO date string (YYYY-MM-DD). */
  examDate: string;
  description?: string | null;
}

export interface QuestionDto {
  id: string;
  examId: string;
  number: number;
  text: string;
  maxScore: number;
  fileName?: string | null;
  contentType?: string | null;
  /** پاسخنامه (model answer) file — M3 management UI. */
  answerKeyFileName?: string | null;
  answerKeyContentType?: string | null;
}

export interface CreateQuestionRequest {
  examId: string;
  number: number;
  text: string;
  maxScore: number;
}

export interface RubricCriterionDto {
  criterionId: string;
  description: string;
  maxScore: number;
  order: number;
}

export interface RubricDto {
  id: string;
  questionId: string;
  version: number;
  isActive: boolean;
  createdAt: string;
  totalMaxScore: number;
  criteria: RubricCriterionDto[];
}

export interface CreateRubricRequest {
  questionId: string;
  criteria: { criterionId: string; description: string; maxScore: number }[];
}

// ── Exam-wide rubric extraction (AI) ─────────────────────────────────────────

export interface ExtractedCriterion {
  criterionId: string;
  description: string;
  maxScore: number;
}

export interface ExtractedQuestion {
  number: number;
  text: string;
  maxScore: number;
  criteria: ExtractedCriterion[];
}

/** POST /api/exams/{id}/extraction/preview response — editable, saves nothing. */
export interface ExtractionPreview {
  examId: string;
  fileName?: string | null;
  contentType?: string | null;
  questions: ExtractedQuestion[];
  warnings: string[];
  provider: string;
  modelName: string;
  inputTokens: number;
  outputTokens: number;
  latencyMs: number;
  estimatedCostUsd: number;
}

export interface ApplyExtractionQuestion {
  number: number;
  text: string;
  maxScore: number;
  criteria: ExtractedCriterion[];
}

/** POST /api/exams/{id}/extraction/apply response. */
export interface ApplyExtractionResult {
  createdQuestions: number;
  updatedQuestions: number;
  rubricsCreated: number;
  questionsUntouched: number;
}

export interface StudentDto {
  id: string;
  externalId: string;
  displayName?: string | null;
  createdAt: string;
}

export interface CreateStudentRequest {
  externalId: string;
  displayName?: string | null;
}

// ── M6 bulk answer-sheet split ──────────────────────────────────────────────

export type BulkPageReviewStatus = "auto" | "needs_review" | "unmatched" | "confirmed" | "skipped";

export interface BulkPageDto {
  id: string;
  pageNumber: number;
  pageArtifactId: string;
  rawOcrId?: string | null;
  ocrConfidence: number;
  matchedStudentId?: string | null;
  matchedStudentExternalId?: string | null;
  matchedStudentDisplayName?: string | null;
  reviewStatus: BulkPageReviewStatus;
  sortInStack: number;
}

export interface BulkBatchDto {
  id: string;
  questionId: string;
  sourceArtifactId: string;
  sourceFileName?: string | null;
  status: "splitting" | "ready_for_review" | "applied" | "failed";
  totalPages: number;
  error?: string | null;
  createdAt: string;
  pages: BulkPageDto[];
}

export interface BulkApplySummary {
  students: number;
  pagesApplied: number;
  pagesSkipped: number;
  pagesUnassigned: number;
}

export type ReviewAction = "Accept" | "Override";

export interface GradingRunSummaryDto {
  id: string;
  provider: string;
  modelName: string;
  promptVersion: string;
  temperature: number;
  aiScore: number;
  teacherScoreSnapshot?: number | null;
  isValid: boolean;
  error?: string | null;
  createdAt: string;
  inputArtifactsJson?: string | null;
}

export interface AnswerDto {
  id: string;
  studentId: string;
  studentExternalId: string;
  questionId: string;
  imageStorageKey: string;
  fileName?: string | null;
  contentType?: string | null;
  teacherScore?: number | null;
  teacher2Score?: number | null;
  uploadedAt: string;
  gradingRuns: GradingRunSummaryDto[];
}

/** Full GradingRun entity (GET /api/grading/run/{id}) */
export interface GradingRun {
  id: string;
  answerId: string;
  questionId: string;
  studentId: string;
  provider: string;
  modelName: string;
  modelVersion?: string | null;
  temperature: number;
  promptVersion: string;
  aiScore: number;
  teacherScoreSnapshot?: number | null;
  rawAiResponse: string;
  isValid: boolean;
  validationErrorsJson?: string | null;
  criteriaScoresJson?: string | null;
  reasoning?: string | null;
  latencyMs: number;
  inputTokens: number;
  outputTokens: number;
  estimatedCost: number;
  error?: string | null;
  createdAt: string;
  /** M1/M4 lineage — the artifacts this run consumed (see parseLineage). */
  inputArtifactsJson?: string | null;
}

export interface CriterionScore {
  criterionId: string;
  score: number;
  maxScore: number;
  comment?: string | null;
}

export function parseCriteriaScores(json?: string | null): CriterionScore[] {
  if (!json) return [];
  try {
    const parsed: unknown = JSON.parse(json);
    if (!Array.isArray(parsed)) return [];
    return parsed
      .filter((c): c is Record<string, unknown> => c != null && typeof c === "object")
      .map((c) => {
        // Tolerate legacy key casings — the API contract is camelCase, but rows
        // persisted by older API builds carry PascalCase keys and the Python
        // service speaks snake_case. One odd row must never blank the page.
        const pick = (...keys: string[]): unknown => {
          for (const key of keys) {
            if (c[key] !== undefined) return c[key];
          }
          return undefined;
        };
        const toNum = (v: unknown): number => {
          const n = typeof v === "number" ? v : Number(v);
          return Number.isFinite(n) ? n : 0;
        };
        const rawId = pick("criterionId", "CriterionId", "criterion_id", "id");
        const rawComment = pick("comment", "Comment");
        return {
          criterionId: rawId != null ? String(rawId) : "unknown",
          score: toNum(pick("score", "Score")),
          maxScore: toNum(pick("maxScore", "MaxScore", "max_score")),
          comment: typeof rawComment === "string" ? rawComment : null,
        };
      });
  } catch {
    return [];
  }
}

/** One lineage entry from GradingRun.inputArtifactsJson (M1/M4). */
export interface LineageEntry {
  kind: "page" | "legacy_file";
  role: "answer" | "question" | "model_answer";
  sha256?: string | null;
  page?: number | null;
  storage_key?: string | null;
}

/**
 * Parse a run's InputArtifactsJson — the exact artifacts the AI consumed.
 * Tolerant: null/old runs yield [], a corrupt row yields [] (one bad row must
 * never blank the run timeline).
 */
export function parseLineage(json?: string | null): LineageEntry[] {
  if (!json) return [];
  try {
    const parsed: unknown = JSON.parse(json);
    if (!Array.isArray(parsed)) return [];
    return parsed
      .filter((e): e is Record<string, unknown> => e != null && typeof e === "object")
      .map((e) => {
        const kind = e.kind === "page" ? "page" : "legacy_file";
        const role =
          e.role === "question"
            ? "question"
            : e.role === "model_answer"
              ? "model_answer"
              : "answer";
        const page = Number(e.page);
        return {
          kind,
          role,
          sha256: typeof e.sha256 === "string" ? e.sha256 : null,
          page: Number.isFinite(page) ? page : null,
          storage_key: typeof e.storage_key === "string" ? e.storage_key : null,
        } satisfies LineageEntry;
      });
  } catch {
    return [];
  }
}

/** M4 golden-set harness — request body for POST /api/evaluation/golden-set. */
export interface GoldenSetRequestDto {
  questionId: string;
  temperature?: number;
  promptVersion?: string;
  maxAnswers?: number;
  note?: string | null;
}

/** M4 golden-set harness — result of one gated A/B diff. */
export interface GoldenSetResultDto {
  evalRunId: string;
  questionId: string;
  baselineCount: number;
  candidateCount: number;
  baselineQwk?: number | null;
  candidateQwk?: number | null;
  qwkDelta?: number | null;
  baselineMae?: number | null;
  candidateMae?: number | null;
  baselineExactAgreementPct?: number | null;
  candidateExactAgreementPct?: number | null;
  regressed: boolean;
  verdict: string;
}

/** One row of GET /api/evaluation/golden-set/history. */
export interface GoldenSetHistoryRow {
  id: string;
  questionId?: string | null;
  configurationJson?: string | null;
  baselineQwk?: number | null;
  baselineMae?: number | null;
  baselineExactAgreementPct?: number | null;
  baselineCount: number;
  candidateQwk?: number | null;
  candidateMae?: number | null;
  candidateExactAgreementPct?: number | null;
  candidateCount: number;
  qwkDelta?: number | null;
  regressed: boolean;
  createdAt: string;
}

/** POST /api/grading/run response */
export interface GradeResultDto {
  runs: GradingRun[];
  totalRuns: number;
  validRuns: number;
  medianAiScore?: number | null;
}

export interface GradeRunRequest {
  answerId: string;
  temperature?: number;
  promptVersion?: string;
  runs?: number;
}

export interface TeacherReviewDto {
  id: string;
  gradingRunId: string;
  teacherUserId: string;
  oldAiScore: number;
  newScore: number;
  note?: string | null;
  reviewedAt: string;
  action: ReviewAction;
}

export interface EvaluationResultDto {
  entityId: string;
  count: number;
  mae: number;
  rmse: number;
  exactAgreementPct: number;
  withinHalfPct: number;
  withinOnePct: number;
  bias: number;
  pearsonR?: number | null;
  quadraticWeightedKappa?: number | null;
  scoreDistribution: Record<string, Record<string, number>>;
}

export interface AuditEntryDto {
  id: string;
  timestamp: string;
  action: string;
  entityType: string;
  entityId?: string | null;
  userId?: string | null;
  details?: string | null;
  ipAddress?: string | null;
}

export interface AuditQueryParams {
  entityId?: string;
  entityType?: string;
  userId?: string;
  from?: string;
  to?: string;
  skip?: number;
  take?: number;
}

export interface HealthDto {
  status: string;
  service: string;
  timestamp: string;
  dependencies: { gradingService: { url: string; up: boolean } };
}