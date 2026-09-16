import { api } from "./client";
import type { GoldenSetRequestDto, GoldenSetResultDto, GoldenSetHistoryRow } from "../types/models";
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

/** M4 golden-set harness: re-grade the frozen teacher-scored set under a
 * candidate config and diff QWK/MAE vs the persisted baseline. Synchronous —
 * MaxAnswers is capped at 50, but each re-grade can take 30-120s, so callers
 * must set a generous axios timeout (the default has none, but requests sit
 * behind slow gateways). */
export async function runGoldenSet(req: GoldenSetRequestDto): Promise<GoldenSetResultDto> {
  const { data } = await api.post<GoldenSetResultDto>("/evaluation/golden-set", req, {
    timeout: 15 * 60 * 1000,
  });
  return data;
}

/** Harness history — newest first, optionally scoped to one question. */
export async function goldenSetHistory(questionId?: string): Promise<GoldenSetHistoryRow[]> {
  const qs = questionId ? `?questionId=${encodeURIComponent(questionId)}` : "";
  const { data } = await api.get<GoldenSetHistoryRow[]>(`/evaluation/golden-set/history${qs}`);
  return data;
}