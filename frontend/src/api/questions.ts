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