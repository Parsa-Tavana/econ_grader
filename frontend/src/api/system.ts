import { api } from "./client";
import type { AuditEntryDto, AuditQueryParams, HealthDto } from "../types/models";

export async function queryAudit(params: AuditQueryParams): Promise<AuditEntryDto[]> {
  const q = new URLSearchParams();
  if (params.entityId) q.set("entityId", params.entityId);
  if (params.entityType) q.set("entityType", params.entityType);
  if (params.userId) q.set("userId", params.userId);
  if (params.from) q.set("from", params.from);
  if (params.to) q.set("to", params.to);
  q.set("skip", String(params.skip ?? 0));
  q.set("take", String(params.take ?? 100));
  const { data } = await api.get<AuditEntryDto[]>(`/audit?${q}`);
  return data;
}

export async function healthCheck(): Promise<HealthDto> {
  const { data } = await api.get<HealthDto>("/health");
  return data;
}