import { api } from "./client";
import type {
  BulkBatchDto,
  BulkPageDto,
  BulkApplySummary,
} from "../types/models";

// ── M6 bulk answer-sheet split ──────────────────────────────────────────────

export async function uploadBulkBatch(
  questionId: string,
  file: File,
): Promise<BulkBatchDto> {
  const form = new FormData();
  form.append("questionId", questionId);
  form.append("file", file);
  // Merged PDFs can be large; the split runs server-side — give the upload a
  // generous window and return immediately with the (probably "splitting")
  // batch; the UI polls GET /{id} until ready_for_review.
  const { data } = await api.post<BulkBatchDto>("/answers/bulk/upload", form, {
    headers: { "Content-Type": "multipart/form-data" },
    timeout: 5 * 60 * 1000,
  });
  return data;
}

export async function getBulkBatch(id: string): Promise<BulkBatchDto> {
  const { data } = await api.get<BulkBatchDto>(`/answers/bulk/${id}`);
  return data;
}

export async function updateBulkPage(
  batchId: string,
  pageId: string,
  patch: { studentId?: string | null; confirm?: boolean; skip?: boolean; clear?: boolean },
): Promise<BulkPageDto> {
  const { data } = await api.patch<BulkPageDto>(
    `/answers/bulk/${batchId}/pages/${pageId}`,
    patch,
  );
  return data;
}

export async function applyBulkBatch(
  batchId: string,
): Promise<BulkApplySummary> {
  const { data } = await api.post<BulkApplySummary>(
    `/answers/bulk/${batchId}/apply`,
  );
  return data;
}
