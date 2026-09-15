import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { FlaskConical } from "lucide-react";
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
import { evaluateExam, evaluateQuestion, goldenSetHistory, runGoldenSet } from "../api/evaluation";
import {
  PageHeader,
  Card,
  CardHeader,
  Badge,
  Button,
  Input,
  Select,
  Field,
  LoadingBlock,
  ErrorState,
  EmptyState,
  friendlyError,
} from "../components/ui";
import { Stat } from "../components/common";
import { formatDateTime, formatNumber, formatScore, toFaDigits } from "../utils/format";
import { currentLang } from "../hooks/useLang";
import { apiErrorMessage } from "../api/client";
import { getAuthUser } from "../api/auth";
import { hasRole } from "../utils/roles";
import type { GoldenSetHistoryRow, GoldenSetResultDto } from "../types/models";

const GREEN = "#10b981";

export default function EvaluationPage() {
  const { t } = useTranslation();
  const lang = currentLang();
  const isTeacher = hasRole(getAuthUser(), "Teacher");

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

          {/* M4 golden-set harness — question scope only (baseline is per-question) */}
          {isTeacher && effectiveQuestionId ? (
            <GoldenSetPanel questionId={effectiveQuestionId} t={t} lang={lang} />
          ) : null}
        </>
      )}
    </>
  );
}

type GoldenSetHistoryEntry = GoldenSetHistoryRow;

function GoldenSetPanel({
  questionId,
  t,
  lang,
}: {
  questionId: string;
  t: (k: string) => string;
  lang: "fa" | "en";
}) {
  const qc = useQueryClient();

  // Harness controls (defaults mirror the API: temp 0, prompt "default", 10 answers)
  const [temperature, setTemperature] = useState(0);
  const [maxAnswers, setMaxAnswers] = useState(10);
  const [note, setNote] = useState("");

  const runMut = useMutation({
    mutationFn: () =>
      runGoldenSet({
        questionId,
        temperature,
        maxAnswers,
        note: note.trim() || undefined,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["golden-set-history", questionId] });
    },
  });

  const historyQ = useQuery({
    queryKey: ["golden-set-history", questionId],
    queryFn: () => goldenSetHistory(questionId),
    retry: false,
  });

  const result = runMut.data as GoldenSetResultDto | undefined;
  const rows: GoldenSetHistoryEntry[] = historyQ.data ?? [];

  return (
    <Card className="mt-5">
      <CardHeader
        title={t("evaluation.goldenSetTitle")}
        subtitle={t("evaluation.goldenSetSubtitle")}
        action={<FlaskConical size={16} className="text-primary-500" />}
      />

      {/* Controls */}
      <div className="grid gap-3 sm:grid-cols-3">
        <Field label={t("gradingDialog.temperature")}>
          <Input
            type="number"
            min={0}
            max={2}
            step={0.1}
            value={temperature}
            onChange={(e) => setTemperature(Number(e.target.value))}
          />
        </Field>
        <Field label={t("evaluation.goldenSetMaxAnswers")} hint={t("evaluation.goldenSetMaxAnswersHint")}>
          <Input
            type="number"
            min={3}
            max={50}
            value={maxAnswers}
            onChange={(e) => setMaxAnswers(Math.min(50, Math.max(3, Number(e.target.value))))}
          />
        </Field>
        <Field label={`${t("reviews.note")} (${t("common.optional")})`}>
          <Input value={note} onChange={(e) => setNote(e.target.value)} maxLength={200} />
        </Field>
      </div>
      <div className="mt-3 flex items-center justify-between gap-3">
        <p className="text-[11px] text-zinc-400">{t("evaluation.goldenSetHint")}</p>
        <Button onClick={() => runMut.mutate()} loading={runMut.isPending}>
          <FlaskConical size={15} /> {t("evaluation.goldenSetRun")}
        </Button>
      </div>

      {/* Error / result banner */}
      {runMut.isError ? (
        <p className="mt-3 rounded-xl bg-red-50 p-3 text-xs text-red-700">
          {friendlyError(apiErrorMessage(runMut.error), t)}
        </p>
      ) : null}
      {result ? (
        <div
          className={`mt-3 rounded-xl p-3 text-sm ${
            result.regressed
              ? "bg-red-50 text-red-800"
              : result.qwkDelta == null
                ? "bg-amber-50 text-amber-800"
                : "bg-emerald-50 text-emerald-800"
          }`}
        >
          <div className="flex flex-wrap items-center gap-2">
            <Badge tone={result.regressed ? "red" : result.qwkDelta == null ? "amber" : "green"}>
              {result.regressed
                ? t("evaluation.goldenSetRegressed")
                : result.qwkDelta == null
                  ? t("evaluation.goldenSetInconclusive")
                  : t("evaluation.goldenSetPass")}
            </Badge>
            <span className="ltr-token tabular-nums">
              QWK{" "}
              {result.baselineQwk != null ? formatNumber(result.baselineQwk, lang) : "—"} →{" "}
              {result.candidateQwk != null ? formatNumber(result.candidateQwk, lang) : "—"} (Δ{" "}
              {result.qwkDelta != null ? formatNumber(result.qwkDelta, lang) : "—"})
            </span>
            <span className="ltr-token tabular-nums">
              MAE {result.baselineMae != null ? formatScore(result.baselineMae, lang) : "—"} →{" "}
              {result.candidateMae != null ? formatScore(result.candidateMae, lang) : "—"}
            </span>
            <span className="ltr-token tabular-nums">
              {t("evaluation.nPairs")} {result.baselineCount}/{result.candidateCount}
            </span>
          </div>
          <p className="mt-2 text-xs ltr-token">{result.verdict}</p>
        </div>
      ) : null}

      {/* History */}
      <div className="mt-5">
        <p className="mb-2 text-xs font-medium text-zinc-500">{t("evaluation.goldenSetHistory")}</p>
        {historyQ.isLoading ? (
          <LoadingBlock />
        ) : historyQ.isError ? (
          <p className="text-xs text-zinc-400">{friendlyError(apiErrorMessage(historyQ.error), t)}</p>
        ) : !rows.length ? (
          <p className="py-3 text-center text-xs text-zinc-400">{t("evaluation.goldenSetNoHistory")}</p>
        ) : (
          <div className="overflow-x-auto rounded-xl border border-zinc-200">
            <table className="w-full text-xs">
              <thead>
                <tr className="bg-zinc-50 text-zinc-500">
                  <th className="px-3 py-2 text-start font-medium">{t("evaluation.goldenSetWhen")}</th>
                  <th className="px-3 py-2 text-end font-medium">QWK</th>
                  <th className="px-3 py-2 text-end font-medium">ΔQWK</th>
                  <th className="px-3 py-2 text-end font-medium">{t("evaluation.mae")}</th>
                  <th className="px-3 py-2 text-end font-medium">{t("evaluation.exactMatch")}</th>
                  <th className="px-3 py-2 text-end font-medium">{t("evaluation.nPairs")}</th>
                  <th className="px-3 py-2 text-end font-medium">{t("evaluation.goldenSetVerdict")}</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.id} className="border-t border-zinc-100">
                    <td className="px-3 py-2 text-zinc-500">{formatDateTime(r.createdAt, lang)}</td>
                    <td className="px-3 py-2 text-end tabular-nums ltr-token">
                      {r.candidateQwk != null ? formatNumber(r.candidateQwk, lang) : "—"}
                    </td>
                    <td className="px-3 py-2 text-end tabular-nums ltr-token">
                      {r.qwkDelta != null ? formatNumber(r.qwkDelta, lang) : "—"}
                    </td>
                    <td className="px-3 py-2 text-end tabular-nums">
                      {r.candidateMae != null ? formatScore(r.candidateMae, lang) : "—"}
                    </td>
                    <td className="px-3 py-2 text-end tabular-nums ltr-token">
                      {r.candidateExactAgreementPct != null
                        ? `${formatNumber(r.candidateExactAgreementPct, lang)}٪`
                        : "—"}
                    </td>
                    <td className="px-3 py-2 text-end tabular-nums">
                      {formatNumber(r.candidateCount, lang)}
                    </td>
                    <td className="px-3 py-2 text-end">
                      <Badge tone={r.regressed ? "red" : "green"}>
                        {r.regressed ? t("evaluation.goldenSetRegressed") : t("evaluation.goldenSetPass")}
                      </Badge>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </Card>
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

  // Sort numerically on the RAW score BEFORE formatting — formatting to
  // Persian digits makes Number() return NaN and breaks chart ordering.
  const distData = Object.entries(ev)
    .map(([ts, aiCounts]) => ({ rawTs: Number(ts), aiCounts }))
    .sort((a, b) => a.rawTs - b.rawTs)
    .map(({ rawTs, aiCounts }) => {
      const row: Record<string, string | number> = { teacher: formatScore(rawTs, lang) };
      for (const ai of aiScores) row[formatScore(ai, lang)] = aiCounts[String(ai)] ?? 0;
      return row;
    });

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
