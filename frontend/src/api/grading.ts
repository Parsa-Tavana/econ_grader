import { api } from "./client";
import type {
  GradingRun,
  GradeRunRequest,
  GradeResultDto,
  TeacherReviewDto,
} from "../types/models";

/** Kick off AI grading — teacher score is NEVER part of this request (blind grading). */
export async function runGrading(req: GradeRunRequest): Promise<GradeResultDto> {
  const { data } = await api.post<GradeResultDto>("/grading/run", req);
  return data;
}
export async function listRunsForAnswer(answerId: string): Promise<GradingRun[]> {
  const { data } = await api.get<GradingRun[]>(`/grading/answer/${answerId}`);
  return data;
}
export async function getRun(runId: string): Promise<GradingRun> {
  const { data } = await api.get<GradingRun>(`/grading/run/${runId}`);
  return data;
}
export async function getPromptVersions(): Promise<string[]> {
  const { data } = await api.get<{ prompts: string[] }>("/grading/prompts");
  return data.prompts;
}

/* ── Teacher review (append-only) ── */
export async function acceptRun(runId: string, note?: string): Promise<TeacherReviewDto> {
  const { data } = await api.post<TeacherReviewDto>(`/grading/${runId}/review/accept`, { note });
  return data;
}
export async function overrideRun(runId: string, newScore: number, note?: string): Promise<TeacherReviewDto> {
  const { data } = await api.post<TeacherReviewDto>(`/grading/${runId}/review/override`, {
    newScore,
    note,
  });
  return data;
}
export async function getReviewHistory(runId: string): Promise<TeacherReviewDto[]> {
  const { data } = await api.get<TeacherReviewDto[]>(`/grading/${runId}/review/history`);
  return data;
}

/** All reviews across every run of one answer (client-side join). */
export async function listReviewsForAnswer(answerId: string): Promise<TeacherReviewDto[]> {
  const runs = await listRunsForAnswer(answerId);
  const histories = await Promise.all(runs.map((r) => getReviewHistory(r.id).catch(() => [])));
  return histories.flat().sort((a, b) => +new Date(b.reviewedAt) - +new Date(a.reviewedAt));
}

/* ── Queue mode (M2) + bulk grading (M3) ─────────────────────────────────── */

export interface GradingJobDto {
  id: string;
  answerId: string;
  temperature: number;
  promptVersion: string;
  ensembleIndex: number;
  status: "pending" | "running" | "completed" | "failed" | "cancelled";
  attempts: number;
  errorKind?: string | null;
  error?: string | null;
  startedAt?: string | null;
  finishedAt?: string | null;
  gradingRunId?: string | null;
  createdAt: string;
}

export interface EnqueueResponse {
  jobs: GradingJobDto[];
  statusUrl: string;
}

/** Poll job statuses for a scope (exam / question / answer). */
export async function listGradingJobs(params: {
  answerId?: string;
  examId?: string;
  questionId?: string;
  status?: string;
}): Promise<GradingJobDto[]> {
  const { data } = await api.get<GradingJobDto[]>("/grading/jobs", { params });
  return data;
}

export interface BulkGradeRequest {
  examId: string;
  questionId?: string;
  temperature?: number;
  promptVersion?: string;
  runs?: number;
}

export interface BulkGradeResponse {
  enqueued: number;
  skipped: number;
  message?: string;
  answers?: { answerId: string; jobs: GradingJobDto[] }[];
  statusUrl?: string;
}

/** Grade all remaining answers of an exam (or one question) — 202. */
export async function bulkGrade(req: BulkGradeRequest): Promise<BulkGradeResponse> {
  const { data } = await api.post<BulkGradeResponse>("/grading/bulk", req);
  return data;
}
