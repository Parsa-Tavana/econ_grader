import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { RefreshCw } from "lucide-react";
import { queryAudit } from "../api/system";
import {
  PageHeader,
  Card,
  Badge,
  Input,
  Button,
  LoadingBlock,
  ErrorState,
  EmptyState,
  friendlyError,
} from "../components/ui";
import { formatDateTime } from "../utils/format";
import { currentLang } from "../hooks/useLang";

export default function AuditPage() {
  const { t } = useTranslation();
  const lang = currentLang();

  const [entityType, setEntityType] = useState("");
  const [userId, setUserId] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");

  // filters are applied on submit to avoid refetch per keystroke
  const [applied, setApplied] = useState({});

  const auditQ = useQuery({
    queryKey: ["audit", applied],
    queryFn: () => queryAudit({ ...applied, take: 200 }),
  });

  function applyFilters() {
    setApplied({
      entityType: entityType || undefined,
      userId: userId || undefined,
      from: from ? new Date(from).toISOString() : undefined,
      to: to ? new Date(to).toISOString() : undefined,
    });
  }

  if (auditQ.isLoading) return <LoadingBlock />;
  if (auditQ.isError)
    return <ErrorState message={friendlyError(auditQ.error, t)} onRetry={() => auditQ.refetch()} />;

  return (
    <>
      <PageHeader
        title={t("audit.title")}
        subtitle={t("audit.subtitle")}
        action={
          <Button variant="secondary" onClick={() => auditQ.refetch()}>
            <RefreshCw size={14} /> {t("common.refresh")}
          </Button>
        }
      />

      {/* Filters */}
      <Card className="mb-5">
        <div className="grid gap-3 md:grid-cols-4" onSubmit={applyFilters}>
          <label className="block">
            <span className="mb-1.5 block text-xs font-medium text-zinc-700">{t("audit.entityTypeFilter")}</span>
            <Input value={entityType} onChange={(e) => setEntityType(e.target.value)} placeholder="Exam / Question / Answer…" />
          </label>
          <label className="block">
            <span className="mb-1.5 block text-xs font-medium text-zinc-700">{t("audit.userFilter")}</span>
            <Input value={userId} onChange={(e) => setUserId(e.target.value)} placeholder="GUID" />
          </label>
          <label className="block">
            <span className="mb-1.5 block text-xs font-medium text-zinc-700">{t("audit.dateFrom")}</span>
            <Input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
          </label>
          <label className="block">
            <span className="mb-1.5 block text-xs font-medium text-zinc-700">{t("audit.dateTo")}</span>
            <Input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
          </label>
        </div>
        <div className="mt-3 flex items-center justify-between">
          <Badge tone="zinc">{t("audit.entriesCount", { count: auditQ.data?.length ?? 0 })}</Badge>
          <Button size="sm" onClick={applyFilters}>
            {t("common.search")}
          </Button>
        </div>
      </Card>

      {/* Table */}
      {!auditQ.data?.length ? (
        <Card>
          <EmptyState title={t("audit.noEntries")} hint={t("audit.noEntriesHint")} />
        </Card>
      ) : (
        <Card className="overflow-x-auto p-0">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-zinc-200 text-xs uppercase tracking-wide text-zinc-400">
                <th className="px-4 py-3 text-start font-medium">{t("audit.timestamp")}</th>
                <th className="px-4 py-3 text-start font-medium">{t("audit.action")}</th>
                <th className="px-4 py-3 text-start font-medium">{t("audit.entityType")}</th>
                <th className="px-4 py-3 text-start font-medium">{t("audit.userId")}</th>
                <th className="px-4 py-3 text-start font-medium">{t("audit.details")}</th>
              </tr>
            </thead>
            <tbody>
              {auditQ.data.map((e) => (
                <tr key={e.id} className="border-b border-zinc-50 transition hover:bg-zinc-50/60">
                  <td className="whitespace-nowrap px-4 py-2.5 text-xs tabular-nums text-zinc-500">
                    {formatDateTime(e.timestamp, lang)}
                  </td>
                  <td className="px-4 py-2.5">
                    <Badge tone="blue">{e.action}</Badge>
                  </td>
                  <td className="px-4 py-2.5 text-zinc-600">
                    {e.entityType}
                    {e.entityId ? (
                      <span className="ltr-token block text-[10px] text-zinc-400">
                        {e.entityId.slice(0, 8)}…
                      </span>
                    ) : null}
                  </td>
                  <td className="px-4 py-2.5 text-xs text-zinc-400">
                    {e.userId ? `${e.userId.slice(0, 8)}…` : "—"}
                  </td>
                  <td className="max-w-[280px] truncate px-4 py-2.5 text-xs text-zinc-500" title={e.details ?? undefined}>
                    {e.details || "—"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}
    </>
  );
}
