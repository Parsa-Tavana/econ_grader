import { api } from "./client";
import type { AnswerDto } from "../types/models";

export async function listAnswersByQuestion(questionId: string): Promise<AnswerDto[]> {
  const { data } = await api.get<AnswerDto[]>(`/answers/by-question/${questionId}`);
  return data;
}
export async function getAnswer(id: string): Promise<AnswerDto> {
  const { data } = await api.get<AnswerDto>(`/answers/${id}`);
  return data;
}
export async function uploadAnswer(
  studentId: string,
  questionId: string,
  file: File,
  teacherScore?: number,
  teacher2Score?: number
): Promise<AnswerDto> {
  const form = new FormData();
  form.append("studentId", studentId);
  form.append("questionId", questionId);
  if (teacherScore !== undefined) form.append("teacherScore", String(teacherScore));
  if (teacher2Score !== undefined) form.append("teacher2Score", String(teacher2Score));
  form.append("file", file);
  const { data } = await api.post<AnswerDto>("/answers/upload", form, {
    headers: { "Content-Type": "multipart/form-data" },
  });
  return data;
}
export function getAnswerImageUrl(answerId: string): string {
  return `/api/answers/${answerId}/image`;
}
export async function setTeacherScore(answerId: string, score: number, teacher2Score?: number): Promise<AnswerDto> {
  const { data } = await api.put<AnswerDto>(`/answers/${answerId}/teacher-score`, { score, teacher2Score });
  return data;
}