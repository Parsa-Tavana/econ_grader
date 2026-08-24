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
