import { api } from "./client";
import type {
  QuestionDto,
  CreateQuestionRequest,
  RubricDto,
  CreateRubricRequest,
} from "../types/models";

export async function listQuestionsByExam(examId: string): Promise<QuestionDto[]> {
  const { data } = await api.get<QuestionDto[]>(`/questions/by-exam/${examId}`);
  return data;
}
export async function getQuestion(id: string): Promise<QuestionDto> {
  const { data } = await api.get<QuestionDto>(`/questions/${id}`);
  return data;
}
export async function createQuestion(req: CreateQuestionRequest): Promise<QuestionDto> {
  const { data } = await api.post<QuestionDto>("/questions", req);
  return data;
}
export async function updateQuestion(
  id: string,
  req: Partial<Pick<CreateQuestionRequest, "text" | "maxScore" | "rubricText">>
): Promise<QuestionDto> {
  const { data } = await api.put<QuestionDto>(`/questions/${id}`, req);
  return data;
}
export async function deleteQuestion(id: string): Promise<void> {
  await api.delete(`/questions/${id}`);
}
export async function getActiveRubric(questionId: string): Promise<RubricDto> {
  const { data } = await api.get<RubricDto>(`/questions/${questionId}/rubric`);
  return data;
}
export async function createRubric(req: CreateRubricRequest): Promise<RubricDto> {
  const { data } = await api.post<RubricDto>(`/questions/${req.questionId}/rubrics`, {
    criteria: req.criteria,
  });
  return data;
}

// ── Question / Rubric file attachments ──────────────────────────────────────

export interface FileMetaDto {
  fileStorageKey: string;
  fileName: string | null;
  contentType: string | null;
}

export async function uploadQuestionFile(questionId: string, file: File): Promise<FileMetaDto> {
  const form = new FormData();
  form.append("file", file);
  const { data } = await api.post<FileMetaDto>(`/questions/${questionId}/file`, form, {
    headers: { "Content-Type": "multipart/form-data" },
  });
  return data;
}
export function questionFileUrl(questionId: string): string {
  return `/api/questions/${questionId}/file`;
}
export async function deleteQuestionFile(questionId: string): Promise<void> {
  await api.delete(`/questions/${questionId}/file`);
}

export async function uploadRubricFile(questionId: string, file: File): Promise<FileMetaDto> {
  const form = new FormData();
  form.append("file", file);
  const { data } = await api.post<FileMetaDto>(`/questions/${questionId}/rubric/file`, form, {
    headers: { "Content-Type": "multipart/form-data" },
  });
  return data;
}
export function rubricFileUrl(questionId: string): string {
  return `/api/questions/${questionId}/rubric/file`;
}
export async function deleteRubricFile(questionId: string): Promise<void> {
  await api.delete(`/questions/${questionId}/rubric/file`);
}
