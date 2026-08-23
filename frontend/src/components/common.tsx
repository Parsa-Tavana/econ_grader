import { useTranslation } from "react-i18next";
import { clsx } from "clsx";
import { AlertTriangle, CheckCircle2, Clock } from "lucide-react";
import type { AnswerDto } from "../types/models";
import { Badge } from "./ui/Card";

/** Derives review status for an answer from its grading runs. */
export function answerStatus(a: AnswerDto): "reviewed" | "pending" | "aiGraded" | "error" | "noAi" {
  const runs = a.gradingRuns ?? [];
  if (!runs.length) return "noAi";
  if (runs.some((r) => r.error && !r.isValid)) return "error";
  // A run with a teacher snapshot means it has been reviewed
  if (runs.some((r) => r.teacherScoreSnapshot !== null && r.teacherScoreSnapshot !== undefined))
    return "reviewed";
  return "aiGraded";
}

export function AnswerStatusBadge({ answer }: { answer: AnswerDto }) {
  const { t } = useTranslation();
  const s = answerStatus(answer);
  switch (s) {
    case "reviewed":
      return (
        <Badge tone="green">
          <CheckCircle2 size={12} />
          {t("status.reviewed")}
        </Badge>
      );
    case "aiGraded":
      return <Badge tone="blue">{t("status.aiGraded")}</Badge>;
    case "pending":
      return (
        <Badge tone="amber">
          <Clock size={12} />
          {t("status.pending")}
        </Badge>
      );
    case "error":
      return (
        <Badge tone="red">
          <AlertTriangle size={12} />
          {t("status.error")}
        </Badge>
      );
    default:
      return <Badge tone="zinc">{t("status.noAi")}</Badge>;
  }
}

/** Small stat tile used across dashboard/evaluation pages. */
export function Stat({
  label,
  value,
  sub,
  tone,
}: {
  label: string;
  value: string;
  sub?: string;
  tone?: "default" | "good" | "warn" | "bad";
}) {
  const tones: Record<string, string> = {
    default: "text-zinc-900",
    good: "text-emerald-600",
    warn: "text-amber-600",
    bad: "text-red-600",
  };
  return (
    <div className="app-card p-4">
      <p className="text-xs font-medium text-zinc-500">{label}</p>
      <p className={clsx("mt-1.5 text-2xl font-bold tabular-nums", tones[tone ?? "default"])}>
        {value}
      </p>
      {sub ? <p className="mt-0.5 text-[11px] text-zinc-400">{sub}</p> : null}
    </div>
  );
}
