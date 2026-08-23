import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  Legend,
  ResponsiveContainer,
  Scatter,
  ScatterChart,
  Tooltip,
  XAxis,
  YAxis,
  ZAxis,
} from "recharts";
import { listExams } from "../api/exams";
import { listQuestionsByExam } from "../api/questions";
import { evaluateExam, evaluateQuestion } from "../api/evaluation";
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
} from "../components/ui";
import { Stat } from "../components/common";
import { formatNumber, formatScore, toFaDigits } from "../utils/format";
import { currentLang } from "../hooks/useLang";

const GREEN = "#10b981";

export default function EvaluationPage() {
  const { t } = useTranslation();
  const lang = currentLang();

  const examsQ = useQuery({ queryKey: ["exams"], queryFn: listExams });
  const [examId, setExamId] = useState("");
  const [questionId, setQuestionId] = useState("");

  const effectiveExamId = examId || examsQ.data?.[0]?.id || "";
  const questionsQ = useQuery({
    queryKey: ["questions", effectiveExamId],
    queryFn: () => listQuestionsByExam(effectiveExamId),
    enabled: !!effectiveExamId,
  });
  const effectiveQuestionId = questionId || questionsQ.data?.[0]?.id || "";

  const evalQ = useQuery({
    queryKey: [
      "evaluation",
      effectiveQuestionId ? `q-${effectiveQuestionId}` : `e-${effectiveExamId}`,
    ],
    queryFn: () =>
      effectiveQuestionId
        ? evaluateQuestion(effectiveQuestionId)
        : evaluateExam(effectiveExamId),
    enabled: !!(effectiveQuestionId || effectiveExamId),
    retry: false,
  });

  if (examsQ.isLoading) return <LoadingBlock />;
  if (examsQ.isError)
    return <ErrorState message={friendlyError(examsQ.error, t)} onRetry={() => examsQ.refetch()} />;

  const ev = evalQ.data;
  const pct = (v: number | null | undefined) =>
    v == null ? "—" : `${formatNumber(v, lang, { maximumFractionDigits: 1 })}٪`;

  return (
    <>
      <PageHeader title={t("evaluation.title")} subtitle={t("evaluation.subtitle")} />

      {/* Scope filters */}
      <Card className="mb-5">
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t("dashboard.examFilter")}>
            <Select
              value={effectiveExamId}
              onChange={(e) => {
                setExamId(e.target.value);
                setQuestionId("");
              }}
            >
              {(examsQ.data ?? []).map((e) => (
                <option key={e.id} value={e.id}>
                  {e.name}
                </option>
              ))}
            </Select>
          </Field>
          <Field label={t("queue.questionFilter")}>
            <Select value={effectiveQuestionId} onChange={(e) => setQuestionId(e.target.value)}>
              <option value="">{t("evaluation.examScope")}</option>
              {(questionsQ.data ?? []).map((q) => (
                <option key={q.id} value={q.id}>
                  {t("questions.questionN", { number: q.number })}
                </option>
              ))}
            </Select>
          </Field>
        </div>
      </Card>

      {evalQ.isLoading ? (
        <LoadingBlock />
      ) : evalQ.isError || !ev || ev.count === 0 ? (
        <Card>
          <EmptyState title={t("evaluation.noEvaluatedData")} hint={t("evaluation.noEvaluatedDataHint")} />
        </Card>
      ) : (
        <>
          {/* Metric tiles */}
          <div className="grid grid-cols-2 gap-3 md:grid-cols-4 xl:grid-cols-8">
            <Stat label={t("evaluation.nPairs")} value={formatNumber(ev.count, lang)} />
            <Stat label={t("evaluation.mae")} value={formatScore(ev.mae, lang)} tone={ev.mae <= 1 ? "good" : "warn"} />
            <Stat label={t("evaluation.rmse")} value={formatScore(ev.rmse, lang)} />
            <Stat label={t("evaluation.exactMatch")} value={pct(ev.exactAgreementPct)} />
            <Stat label={t("evaluation.withinHalf")} value={pct(ev.withinHalfPct)} />
            <Stat label={t("evaluation.withinOne")} value={pct(ev.withinOnePct)} />
            <Stat
              label={t("evaluation.bias")}
              value={formatScore(ev.bias, lang)}
              tone={Math.abs(ev.bias) <= 0.5 ? "good" : "warn"}
            />
            <Stat
              label="Pearson r"
              value={ev.pearsonR != null ? formatNumber(ev.pearsonR, lang) : "—"}
            />
          </div>

          <Charts ev={ev.scoreDistribution} t={t} lang={lang} />

          <p className="mt-3 text-center text-[11px] text-zinc-400 ltr-token">
            {toFaDigits(`n = ${ev.count}`)} · QWK:{" "}
            {ev.quadraticWeightedKappa != null ? formatNumber(ev.quadraticWeightedKappa, lang) : "—"}
          </p>
        </>
      )}
    </>
  );
}

type DistMatrix = Record<string, Record<string, number>>;

function Charts({ ev, t, lang }: { ev: DistMatrix; t: (k: string) => string; lang: "fa" | "en" }) {
  // matrix → stacked bar data (teacher score on x, AI counts stacked)
  const aiScores = Array.from(
    new Set(Object.values(ev).flatMap((m) => Object.keys(m)))
  )
    .map(Number)
    .sort((a, b) => a - b);

  const distData = Object.entries(ev)
    .map(([ts, aiCounts]) => {
      const row: Record<string, string | number> = { teacher: formatScore(Number(ts), lang) };
      for (const ai of aiScores) row[formatScore(ai, lang)] = aiCounts[String(ai)] ?? 0;
      return row;
    })
    .sort((a, b) => Number(a.teacher) - Number(b.teacher));

  const scatterData = Object.entries(ev).flatMap(([ts, aiCounts]) =>
    Object.entries(aiCounts)
      .filter(([, n]) => n > 0)
      .map(([as, n]) => ({ teacher: Number(ts), ai: Number(as), n }))
  );

  return (
    <div className="mt-4 grid gap-4 lg:grid-cols-2">
      <Card>
        <CardHeader title={t("evaluation.distributionTitle")} subtitle={t("dashboard.scoreDistribution")} />
        <div className="h-72">
          {distData.length && aiScores.length ? (
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={distData} margin={{ top: 5, right: 8, left: -18, bottom: 5 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="#f1f1f3" />
                <XAxis dataKey="teacher" tick={{ fontSize: 11 }} />
                <YAxis allowDecimals={false} tick={{ fontSize: 11 }} />
                <Tooltip wrapperStyle={{ fontSize: 11 }} />
                <Legend wrapperStyle={{ fontSize: 10 }} />
                {aiScores.map((ai, i) => (
                  <Bar
                    key={ai}
                    dataKey={formatScore(ai, lang)}
                    stackId="ai"
                    fill={`hsl(${150 + i * 18}, 52%, ${42 + (i % 4) * 9}%)`}
                    radius={[2, 2, 0, 0]}
                  />
                ))}
              </BarChart>
            </ResponsiveContainer>
          ) : (
            <EmptyState title={t("common.noResults")} />
          )}
        </div>
      </Card>

      <Card>
        <CardHeader title={t("evaluation.scatterTitle")} subtitle={t("dashboard.aiVsTeacherAgreement")} />
        <div className="h-72">
          {scatterData.length ? (
            <ResponsiveContainer width="100%" height="100%">
              <ScatterChart margin={{ top: 5, right: 8, left: -18, bottom: 5 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="#f1f1f3" />
                <XAxis type="number" dataKey="teacher" name="Teacher" tick={{ fontSize: 11 }} />
                <YAxis type="number" dataKey="ai" name="AI" tick={{ fontSize: 11 }} />
                <ZAxis type="number" dataKey="n" range={[60, 400]} />
                <Tooltip cursor={{ strokeDasharray: "3 3" }} wrapperStyle={{ fontSize: 11 }} />
                <Scatter data={scatterData} fill="#6366f1" fillOpacity={0.6}>
                  {scatterData.map((d, i) => (
                    <Cell key={i} fill={Math.abs(d.teacher - d.ai) <= 0.5 ? GREEN : "#6366f1"} />
                  ))}
                </Scatter>
              </ScatterChart>
            </ResponsiveContainer>
          ) : (
            <EmptyState title={t("common.noResults")} />
          )}
        </div>
      </Card>
    </div>
  );
}
