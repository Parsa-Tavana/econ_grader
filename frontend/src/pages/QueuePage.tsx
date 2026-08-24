import { useQuery } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { ArrowRight, Filter } from "lucide-react";
import { listExams } from "../api/exams";
import { listQuestionsByExam } from "../api/questions";
import { listAnswersByQuestion } from "../api/answers";
import {
  PageHeader,
  Card,
  CardHeader,
  Select,
  Field,
  LoadingBlock,
  ErrorState,
  EmptyState,
  friendlyError,
  traceIdOf,
} from "../components/ui";
import { AnswerStatusBadge } from "../components/common";
import { formatScore } from "../utils/format";
import { currentLang } from "../hooks/useLang";

export default function QueuePage() {
  const { t } = useTranslation();
  const lang = currentLang();
  const navigate = useNavigate();
  const [params] = useSearchParams();

  const examsQ = useQuery({ queryKey: ["exams"], queryFn: listExams });
  const [examId, setExamId] = useState<string>(params.get("examId") ?? "");
  const [questionId, setQuestionId] = useState<string>("");
  const [statusFilter, setStatusFilter] = useState<string>("all");


  const effectiveExamId = examId || params.get("examId") || "";
  const questionsQ = useQuery({
    queryKey: ["questions", effectiveExamId],
    queryFn: () => listQuestionsByExam(effectiveExamId),
    enabled: !!effectiveExamId,
  });

  const effectiveQuestionId =
    questionId || params.get("questionId") || questionsQ.data?.[0]?.id || "";

  const answersQ = useQuery({
    queryKey: ["answers", "question", effectiveQuestionId],
    queryFn: () => listAnswersByQuestion(effectiveQuestionId),
    enabled: !!effectiveQuestionId,
  });

  const filtered = useMemo(() => {
    const all = answersQ.data ?? [];
    if (statusFilter === "all") return all;
    return all.filter((a) => {
      const runs = a.gradingRuns ?? [];
      switch (statusFilter) {
        case "reviewed":
          return runs.some((r) => r.teacherScoreSnapshot != null);
        case "aiGraded":
          return runs.length > 0 && runs.every((r) => r.teacherScoreSnapshot == null);
        case "noAi":
          return runs.length === 0;
        default:
          return true;
      }
    });
  }, [answersQ.data, statusFilter]);

  if (examsQ.isLoading) return <LoadingBlock />;
  if (examsQ.isError)
    return <ErrorState message={friendlyError(examsQ.error, t)} onRetry={() => examsQ.refetch()} />;

  return (
    <>
      <PageHeader title={t("queue.title")} subtitle={t("queue.subtitle")} />

      {/* Filters */}
      <Card className="mb-5">
        <CardHeader
          title={t("queue.filters")}
          action={<Filter size={15} className="text-zinc-400" />}
        />
        <div className="grid gap-3 sm:grid-cols-3">
          <Field label={t("dashboard.examFilter")}>
            <Select
              value={effectiveExamId}
              onChange={(e) => {
                setExamId(e.target.value);
                setQuestionId("");
              }}
            >
              <option value="">—</option>
              {(examsQ.data ?? []).map((e) => (
                <option key={e.id} value={e.id}>
                  {e.name}
                </option>
              ))}
            </Select>
          </Field>
          <Field label={t("queue.questionFilter")}>
            <Select
              value={effectiveQuestionId}
              onChange={(e) => setQuestionId(e.target.value)}
              disabled={!effectiveExamId}
            >
              {(questionsQ.data ?? []).map((q) => (
                <option key={q.id} value={q.id}>
                  {t("questions.questionN", { number: q.number })}
                </option>
              ))}
            </Select>
          </Field>
          <Field label={t("common.status")}>
            <Select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}>
              <option value="all">{t("common.all")}</option>
              <option value="aiGraded">{t("status.aiGraded")}</option>
              <option value="reviewed">{t("status.reviewed")}</option>
              <option value="noAi">{t("status.noAi")}</option>
            </Select>
          </Field>
        </div>
      </Card>

      {/* Answer list */}
      {!effectiveQuestionId ? (
        <Card>
          <EmptyState title={t("queue.selectQuestionFirst")} hint={t("queue.selectHint")} />
        </Card>
      ) : answersQ.isLoading ? (
        <LoadingBlock />
      ) : answersQ.isError ? (
        <ErrorState
          message={`${friendlyError(answersQ.error, t)}${traceIdOf(answersQ.error) ? ` — ${t("common.traceId")}: ${traceIdOf(answersQ.error)}` : ""}`}
          onRetry={() => answersQ.refetch()}
        />
      ) : !filtered.length ? (
        <Card>
          <EmptyState title={t("queue.noAnswers")} hint={t("queue.uploadFromExamHint")} />
        </Card>
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {filtered.map((a) => {
            const latest = [...(a.gradingRuns ?? [])].sort(
              (x, y) => +new Date(y.createdAt) - +new Date(x.createdAt)
            )[0];
            return (
              <button
                key={a.id}
                onClick={() => navigate(`/grading/workspace/${a.id}`)}
                className="app-card p-4 text-start shadow-sm transition hover:border-primary-300 hover:shadow"
              >
                <div className="mb-2 flex items-center justify-between gap-2">
                  <span className="font-medium text-zinc-800">
                    {a.studentExternalId}
                  </span>
                  <AnswerStatusBadge answer={a} />
                </div>
                <div className="flex items-center justify-between text-xs text-zinc-500">
                  <span>
                    AI:{' '}
                    <strong className="tabular-nums">
                      {latest ? formatScore(latest.aiScore, lang) : "—"}
                    </strong>
                  </span>
                  <span>
                    {t("students.teacherScore")}:{' '}
                    <strong className="tabular-nums">
                      {a.teacherScore != null ? formatScore(a.teacherScore, lang) : "—"}
                    </strong>
                  </span>
                </div>
                <div className="mt-3 flex items-center justify-end gap-1 text-xs font-medium text-primary-600">
                  {t("workspace.openWorkspace")}
                  <ArrowRight size={13} />
                </div>
              </button>
            );
          })}
        </div>
      )}

      <p className="mt-6 text-center text-[11px] text-zinc-400">
        <span>{t("exams.title")}</span> → {t("answers.uploadAnswer")}
      </p>
    </>
  );
}
