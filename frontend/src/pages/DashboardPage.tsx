import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { BookOpen, GraduationCap, FileCheck2, Bot, Hourglass } from "lucide-react";
import { listExams } from "../api/exams";
import { listStudents } from "../api/students";
import { listQuestionsByExam } from "../api/questions";
import { evaluateExam } from "../api/evaluation";
import {
  PageHeader,
  Card,
  CardHeader,
  Badge,
  ProgressBar,
  LoadingBlock,
  ErrorState,
  EmptyState,
  friendlyError,
} from "../components/ui";
import { Stat } from "../components/common";
import { formatNumber, formatScore, timeAgo } from "../utils/format";
import { currentLang } from "../hooks/useLang";

export default function DashboardPage() {
  const { t } = useTranslation();
  const lang = currentLang();

  const examsQ = useQuery({ queryKey: ["exams"], queryFn: listExams });
  const studentsQ = useQuery({ queryKey: ["students"], queryFn: listStudents });

  const examId = examsQ.data?.[0]?.id;
  const questionsQ = useQuery({
    queryKey: ["questions", examId],
    queryFn: () => listQuestionsByExam(examId!),
    enabled: !!examId,
  });
  const evalQ = useQuery({
    queryKey: ["eval-exam", examId],
    queryFn: () => evaluateExam(examId!),
    enabled: !!examId,
    retry: false,
  });

  if (examsQ.isLoading || studentsQ.isLoading) return <LoadingBlock />;
  if (examsQ.isError)
    return (
      <ErrorState
        message={friendlyError(examsQ.error, t)}
        onRetry={() => examsQ.refetch()}
      />
    );

  const ev = evalQ.data;

  return (
    <>
      <PageHeader title={t("dashboard.title")} subtitle={t("dashboard.subtitle")} />

      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
        <Stat label={t("dashboard.examsCount")} value={formatNumber(examsQ.data?.length ?? 0, lang)} />
        <Stat label={t("dashboard.questionsCount")} value={formatNumber(questionsQ.data?.length ?? 0, lang)} />
        <Stat label={t("dashboard.studentsCount")} value={formatNumber(studentsQ.data?.length ?? 0, lang)} />
        <Stat label={t("dashboard.aiGradedCount")} value={formatNumber(ev?.count ?? null, lang)} />
        <Stat
          label={t("dashboard.agreementRate")}
          value={ev ? `${formatNumber(ev.exactAgreementPct, lang, { maximumFractionDigits: 1 })}٪` : "—"}
          tone={ev && ev.exactAgreementPct >= 70 ? "good" : "warn"}
          sub={`±0.5: ${ev ? formatNumber(ev.withinHalfPct, lang, { maximumFractionDigits: 1 }) : "—"}٪`}
        />
        <Stat label={t("dashboard.mae")} value={ev ? formatScore(ev.mae, lang) : "—"} tone={ev && ev.mae <= 1 ? "good" : "warn"} />
      </div>

      <div className="mt-6 grid gap-4 lg:grid-cols-2">
        {/* Recent exams */}
        <Card>
          <CardHeader
            title={t("dashboard.recentExams")}
            action={
              <Link to="/exams" className="text-xs font-medium text-primary-600 hover:underline">
                {t("common.viewDetails")}
              </Link>
            }
          />
          {!examsQ.data?.length ? (
            <EmptyState title={t("exams.noExams")} hint={t("exams.noExamsHint")} />
          ) : (
            <ul className="divide-y divide-zinc-100">
              {examsQ.data.slice(0, 5).map((e) => (
                <li key={e.id}>
                  <Link
                    to={`/exams/${e.id}`}
                    className="-mx-2 flex items-center justify-between gap-3 rounded-lg px-2 py-2.5 transition hover:bg-zinc-50"
                  >
                    <div className="flex items-center gap-3">
                      <BookOpen className="h-4 w-4 shrink-0 text-zinc-400" />
                      <div>
                        <p className="text-sm font-medium text-zinc-800">{e.name}</p>
                        <p className="text-[11px] text-zinc-400">
                          {formatNumber(e.year, lang)} · {timeAgo(e.createdAt, lang)}
                        </p>
                      </div>
                    </div>
                    <Badge tone="zinc">{e.createdByName}</Badge>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </Card>

        {/* AI vs teacher summary */}
        <Card>
          <CardHeader title={t("dashboard.aiVsTeacherAgreement")} />
          {!ev ? (
            <EmptyState title={t("dashboard.noDataYet")} hint={t("dashboard.selectExamHint")} />
          ) : (
            <div className="grid grid-cols-2 gap-3">
              <div className="rounded-xl bg-zinc-50 p-3">
                <div className="flex items-center gap-2 text-xs text-zinc-500">
                  <FileCheck2 size={14} /> {t("dashboard.gradedAnswers")}
                </div>
                <p className="mt-1 text-xl font-bold tabular-nums">{formatNumber(ev.count, lang)}</p>
              </div>
              <div className="rounded-xl bg-zinc-50 p-3">
                <div className="flex items-center gap-2 text-xs text-zinc-500">
                  <Hourglass size={14} /> RMSE
                </div>
                <p className="mt-1 text-xl font-bold tabular-nums">{formatScore(ev.rmse, lang)}</p>
              </div>
              <div className="rounded-xl bg-zinc-50 p-3">
                <div className="flex items-center gap-2 text-xs text-zinc-500">
                  <Bot size={14} /> {t("evaluation.exactMatch")}
                </div>
                <p className="mt-1 text-xl font-bold tabular-nums">
                  {formatNumber(ev.exactAgreementPct, lang, { maximumFractionDigits: 1 })}٪
                </p>
              </div>
              <div className="rounded-xl bg-zinc-50 p-3">
                <div className="text-xs text-zinc-500">QWK</div>
                <p className="mt-1 text-xl font-bold tabular-nums">
                  {ev.quadraticWeightedKappa != null
                    ? formatNumber(ev.quadraticWeightedKappa, lang)
                    : "—"}
                </p>
              </div>
              <div className="col-span-2">
                <p className="mb-1 flex items-center justify-between text-[11px] text-zinc-500">
                  <span>±0.5</span>
                  <span>{formatNumber(ev.withinHalfPct, lang, { maximumFractionDigits: 1 })}٪</span>
                </p>
                <ProgressBar value={ev.withinHalfPct} max={100} tone="green" />
              </div>
            </div>
          )}
        </Card>
      </div>

      {/* Students strip */}
      <Card className="mt-4">
        <CardHeader
          title={t("dashboard.recentActivity")}
          action={
            <Link to="/students" className="text-xs font-medium text-primary-600 hover:underline">
              {t("common.viewDetails")}
            </Link>
          }
        />
        {studentsQ.data?.length ? (
          <div className="flex flex-wrap gap-2">
            {studentsQ.data.slice(-8).map((s) => (
              <Link key={s.id} to={`/students/${s.id}`}>
                <Badge tone="blue">
                  <GraduationCap size={12} />
                  {s.displayName || s.externalId}
                </Badge>
              </Link>
            ))}
          </div>
        ) : (
          <EmptyState title={t("students.noStudents")} hint={t("students.noStudentsHint")} />
        )}
      </Card>
    </>
  );
}
