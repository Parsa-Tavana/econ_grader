import { api } from "./client";
import type { ExamDto, CreateExamRequest, UpdateExamRequest } from "../types/models";

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