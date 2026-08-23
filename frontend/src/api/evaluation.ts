import { api } from "./client";
import type { EvaluationResultDto } from "../types/models";

export async function evaluateQuestion(
  questionId: string,
  provider?: string,
  modelName?: string
): Promise<EvaluationResultDto> {
  const params = new URLSearchParams();
  if (provider) params.set("provider", provider);
  if (modelName) params.set("modelName", modelName);
  const qs = params.toString();
  const { data } = await api.get<EvaluationResultDto>(
    `/evaluation/question/${questionId}${qs ? `?${qs}` : ""}`
  );
  return data;
}
export async function evaluateExam(examId: string): Promise<EvaluationResultDto> {
  const { data } = await api.get<EvaluationResultDto>(`/evaluation/exam/${examId}`);
  return data;
}