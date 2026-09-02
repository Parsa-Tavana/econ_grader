import { api } from "./client";
import type {
  ExamDto,
  CreateExamRequest,
  UpdateExamRequest,
  ExtractionPreview,
  ApplyExtractionQuestion,
  ApplyExtractionResult,
} from "../types/models";

export async function listExams(): Promise<ExamDto[]> {
  const { data } = await api.get<ExamDto[]>("/exams");
  return data;
}
export async function getExam(id: string): Promise<ExamDto> {
  const { data } = await api.get<ExamDto>(`/exams/${id}`);
  return data;
}
export async function createExam(req: CreateExamRequest): Promise<ExamDto> {
  const { data } = await api.post<ExamDto>("/exams", req);
  return data;
}
export async function updateExam(id: string, req: UpdateExamRequest): Promise<ExamDto> {
  const { data } = await api.put<ExamDto>(`/exams/${id}`, req);
  return data;
}
export async function deleteExam(id: string): Promise<void> {
  await api.delete(`/exams/${id}`);
}

// ── Exam-wide rubric file (grading key) + AI extraction ─────────────────────

export interface ExamRubricFileMetaDto {
  fileName: string | null;
  contentType: string | null;
}

export async function uploadExamRubricFile(examId: string, file: File): Promise<ExamRubricFileMetaDto> {
  const form = new FormData();
  form.append("file", file);
  const { data } = await api.post<ExamRubricFileMetaDto>(`/exams/${examId}/rubric/file`, form, {
    headers: { "Content-Type": "multipart/form-data" },
  });
  return data;
}
/** Authenticated stream URL — pass to fetchAuthenticatedFile, never <a href>. */
export function examRubricFileUrl(examId: string): string {
  return `/exams/${examId}/rubric/file`;
}
export async function deleteExamRubricFile(examId: string): Promise<void> {
  await api.delete(`/exams/${examId}/rubric/file`);
}

/** Run AI extraction — returns an editable preview, saves nothing. */
export async function extractExamQuestions(examId: string): Promise<ExtractionPreview> {
  const { data } = await api.post<ExtractionPreview>(`/exams/${examId}/extraction/preview`);
  return data;
}
/** Persist confirmed rows: update-by-number, create missing, never delete others. */
export async function applyExtraction(
  examId: string,
  questions: ApplyExtractionQuestion[]
): Promise<ApplyExtractionResult> {
  const { data } = await api.post<ApplyExtractionResult>(`/exams/${examId}/extraction/apply`, { questions });
  return data;
}