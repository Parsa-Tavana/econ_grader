import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import {
  Bot,
  Check,
  Pencil,
  Play,
  ShieldCheck,
} from "lucide-react";
import {
  getAnswer,
  getAnswerImageUrl,
  setTeacherScore as apiSetTeacherScore,
  listAnswersByQuestion,
} from "../api/answers";
import { getQuestion, getActiveRubric } from "../api/questions";
import {
  runGrading,
  listRunsForAnswer,
  listReviewsForAnswer,
  acceptRun,
  overrideRun,
} from "../api/grading";
import type { TeacherReviewDto } from "../types/models";
import { getUserId, isValidGuid, setUserId } from "../api/client";
import type { GradingRun } from "../types/models";
import { parseCriteriaScores } from "../types/models";
import {
  PageHeader,
  Card,
  CardHeader,
  Badge,
  Select,
  Input,
  Field,
  Button,
  LoadingBlock,
  ErrorState,
  Dialog,
  friendlyError,
} from "../components/ui";
import { AnswerStatusBadge } from "../components/common";
import { formatCost, formatLatency, formatNumber, formatScore, formatDateTime, timeAgo } from "../utils/format";
import { currentLang } from "../hooks/useLang";
import { useToast } from "../hooks/useToast";

export default function WorkspacePage() {
  const { answerId = "" } = useParams();
  const { t } = useTranslation();
  const lang = currentLang();
  const qc = useQueryClient();
  const toast = useToast();
  const navigate = useNavigate();

  const answerQ = useQuery({ queryKey: ["answer", answerId], queryFn: () => getAnswer(answerId) });
  const questionQ = useQuery({
    queryKey: ["question-single", answerQ.data?.questionId],
    queryFn: () => getQuestion(answerQ.data!.questionId),
    enabled: !!answerQ.data?.questionId,
  });
  const rubricQ = useQuery({
    queryKey: ["rubric", answerQ.data?.questionId],
    queryFn: () => getActiveRubric(answerQ.data!.questionId),
    enabled: !!answerQ.data?.questionId,
    retry: false,
  });
  const runsQ = useQuery({
    queryKey: ["runs", answerId],
    queryFn: () => listRunsForAnswer(answerId),
  });
  const reviewsQ = useQuery({
    queryKey: ["reviews", answerId],
    queryFn: () => listReviewsForAnswer(answerId),
  });

  // Sibling answers of the same question → prev/next navigation
  const siblingsQ = useQuery({
    queryKey: ["answers", "question", answerQ.data?.questionId],
    queryFn: () => listAnswersByQuestion(answerQ.data!.questionId),
    enabled: !!answerQ.data?.questionId,
  });
  const siblings = useMemo(
    () => [...(siblingsQ.data ?? [])].sort((a, b) => a.studentExternalId.localeCompare(b.studentExternalId)),
    [siblingsQ.data]
  );
  const idx = siblings.findIndex((a) => a.id === answerId);
  const prevAnswer = idx > 0 ? siblings[idx - 1] : null;
  const nextAnswer = idx >= 0 && idx < siblings.length - 1 ? siblings[idx + 1] : null;

  // grading controls
  const [provider, setProvider] = useState("");
  const [temperature, setTemperature] = useState(0);
  const [runCount, setRunCount] = useState(3);

  // review dialog state
  const [reviewMode, setReviewMode] = useState<"accept" | "override" | null>(null);
  const [overrideScore, setOverrideScore] = useState<number>(0);
  const [note, setNote] = useState("");
  // identity captured inside the review dialog when no valid GUID is configured
  const [identityInput, setIdentityInput] = useState(getUserId() ?? "");
  const [identityError, setIdentityError] = useState<string | null>(null);

  const latestValid = useMemo(
    () =>
      (runsQ.data ?? [])
        .filter((r) => r.isValid)
        .sort((x, y) => +new Date(y.createdAt) - +new Date(x.createdAt))[0],
    [runsQ.data]
  );

  const runMut = useMutation({
    mutationFn: () =>
      runGrading({
        answerId,
        provider: provider || null,
        temperature,
        runs: runCount,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["runs", answerId] });
      qc.invalidateQueries({ queryKey: ["answer", answerId] });
      toast.info(t("states.gradingStarted"));
    },
    onError: (e) => toast.error(friendlyError(e, t)),
  });

  /**
   * Review flow:
   *   1. accept/override the run (append-only review record) — required
   *   2. best-effort sync of the answer's canonical teacher score.
   * Step 2 failing no longer loses the review; the user is told to re-save
   * the score manually instead of getting a confusing partial-failure alert.
   */
  const reviewMut = useMutation({
    mutationFn: async () => {
      if (!latestValid || !reviewMode) return;
      if (reviewMode === "accept") await acceptRun(latestValid.id, note || undefined);
      else await overrideRun(latestValid.id, overrideScore, note || undefined);

      const score = reviewMode === "accept" ? latestValid.aiScore : overrideScore;
      try {
        await apiSetTeacherScore(answerId, score);
      } catch (syncErr) {
        // The review itself is already recorded — surface the score-sync
        // failure distinctly and keep the dialog open so nothing is lost.
        toast.error(
          `${t("workspace.reviewRecorded")} — ${friendlyError(syncErr, t)}`
        );
        throw new Error("SCORE_SYNC_FAILED");
      }
    },
    onSuccess: (_data, _vars, ctx) => {
      if (ctx === "SCORE_SYNC_FAILED") return; // handled above; keep dialog state
      qc.invalidateQueries({ queryKey: ["runs", answerId] });
      qc.invalidateQueries({ queryKey: ["answer", answerId] });
      setReviewMode(null);
      setNote("");
      toast.success(t("states.reviewSaved"));
    },
    onError: (e) => {
      if (e instanceof Error && e.message === "SCORE_SYNC_FAILED") {
        qc.invalidateQueries({ queryKey: ["runs", answerId] });
        qc.invalidateQueries({ queryKey: ["answer", answerId] });
        return;
      }
      toast.error(friendlyError(e, t));
    },
  });

  function openReview(mode: "accept" | "override") {
    if (!latestValid) return;
    setIdentityInput(getUserId() ?? "");
    setIdentityError(isValidGuid(getUserId()) ? null : t("settings.userIdInvalid"));
    setReviewMode(mode);
  }

  /** Blocks submit until a valid GUID identity is present (backend requires X-User-Id). */
  function handleReviewSubmit(e: React.FormEvent) {
    e.preventDefault();
    const value = identityInput.trim();
    if (!isValidGuid(value)) {
      setIdentityError(t("settings.userIdInvalid"));
      return;
    }
    setIdentityError(null);
    setUserId(value);
    reviewMut.mutate();
  }

  if (answerQ.isLoading) return <LoadingBlock />;
  if (answerQ.isError)
    return <ErrorState message={friendlyError(answerQ.error, t)} onRetry={() => answerQ.refetch()} />;

  const answer = answerQ.data!;
  const question = questionQ.data;
  const criteriaScores = latestValid ? parseCriteriaScores(latestValid.criteriaScoresJson) : [];
  const identityReady = isValidGuid(identityInput.trim());

  return (
    <>
      <PageHeader
        title={t("workspace.title")}
        subtitle={`${answer.studentExternalId} · ${question ? t("questions.questionN", { number: question.number }) : ""}`}
        action={
          <div className="flex items-center gap-2">
            <AnswerStatusBadge answer={answer} />
          </div>
        }
      />

      <div className="grid gap-4 xl:grid-cols-2">
        {/* ── Answer image pane ── */}
        <Card className="p-3">
          <div className="mb-2 flex items-center justify-between px-1">
            <h3 className="text-sm font-semibold text-zinc-900">{t("viewer.answerScan")}</h3>
            {question ? (
              <Badge tone="zinc">
                {t("questions.maxScore")}: {formatScore(question.maxScore, lang)}
              </Badge>
            ) : null}
          </div>
          <div className="overflow-hidden rounded-xl bg-zinc-100">
            <img
              src={getAnswerImageUrl(answer.id)}
              alt={`${t("viewer.answerScan")} — ${answer.studentExternalId}`}
              className="max-h-[560px] w-full object-contain"
              loading="lazy"
            />
          </div>
          {question ? (
            <details className="mt-3 rounded-xl border border-zinc-200 p-3 text-sm">
              <summary className="cursor-pointer font-medium text-zinc-700">
                {t("questions.text")}
              </summary>
              <p className="mt-2 leading-relaxed text-zinc-600">{question.text}</p>
              {rubricQ.data ? (
                <ul className="mt-3 space-y-1.5 border-t border-zinc-100 pt-3 text-xs">
                  {rubricQ.data.criteria.map((c) => (
                    <li key={c.criterionId} className="flex justify-between gap-3">
                      <span className="text-zinc-600">{c.description}</span>
                      <span className="shrink-0 font-medium tabular-nums text-zinc-500">
                        {formatScore(c.maxScore, lang)}
                      </span>
                    </li>
                  ))}
                </ul>
              ) : null}
            </details>
          ) : null}
        </Card>

        {/* ── AI result + review pane ── */}
        <div className="space-y-4">
          {/* Latest AI score card */}
          <Card>
            <CardHeader
              title={t("workspace.latestAiResult")}
              action={
                latestValid ? (
                  <Badge tone={latestValid.isValid ? "green" : "red"}>
                    <ShieldCheck size={11} />
                    {latestValid.isValid ? t("grading.valid") : t("status.error")}
                  </Badge>
                ) : null
              }
            />
            {!latestValid ? (
              <p className="py-6 text-center text-sm text-zinc-400">{t("states.noAiResult")}</p>
            ) : (
              <>
                <div className="mb-3 grid grid-cols-3 gap-3">
                  <div className="rounded-xl bg-primary-50 p-3 text-center">
                    <p className="text-[11px] font-medium text-primary-700">AI</p>
                    <p className="mt-1 text-2xl font-bold tabular-nums text-primary-800">
                      {formatScore(latestValid.aiScore, lang)}
                    </p>
                  </div>
                  <div className="rounded-xl bg-zinc-50 p-3 text-center">
                    <p className="text-[11px] font-medium text-zinc-500">{t("students.teacherScore")}</p>
                    <p className="mt-1 text-2xl font-bold tabular-nums text-zinc-700">
                      {answer.teacherScore != null ? formatScore(answer.teacherScore, lang) : "—"}
                    </p>
                  </div>
                  <div className="rounded-xl bg-zinc-50 p-3 text-center">
                    <p className="text-[11px] font-medium text-zinc-500">{t("workspace.diff")}</p>
                    <p className="mt-1 text-2xl font-bold tabular-nums text-zinc-700">
                      {answer.teacherScore != null
                        ? formatScore(Math.abs(latestValid.aiScore - answer.teacherScore), lang)
                        : "—"}
                    </p>
                  </div>
                </div>

                {criteriaScores.length ? (
                  <div className="overflow-x-auto rounded-xl border border-zinc-200">
                    <table className="w-full text-xs">
                      <thead>
                        <tr className="bg-zinc-50 text-zinc-500">
                          <th className="px-3 py-2 text-start font-medium">{t("rubric.criterionDescription")}</th>
                          <th className="px-3 py-2 text-end font-medium">{t("workspace.score")}</th>
                        </tr>
                      </thead>
                      <tbody>
                        {criteriaScores.map((cs, i) => (
                          <tr key={`${cs.criterionId}-${i}`} className="border-t border-zinc-100">
                            <td className="max-w-[280px] truncate px-3 py-2 text-zinc-600" title={cs.comment ?? undefined}>
                              {rubricQ.data?.criteria.find((c) => c.criterionId === cs.criterionId)?.description ??
                                cs.criterionId.slice(0, 8)}
                            </td>
                            <td className="px-3 py-2 text-end font-medium tabular-nums">
                              {formatScore(cs.score, lang)} / {formatScore(cs.maxScore, lang)}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                ) : null}

                {latestValid.reasoning ? (
                  <details className="mt-3 rounded-xl border border-zinc-200 p-3 text-sm">
                    <summary className="cursor-pointer font-medium text-zinc-700">
                      {t("workspace.reasoning")}
                    </summary>
                    <p className="ltr-token mt-2 whitespace-pre-wrap leading-relaxed text-zinc-600">
                      {latestValid.reasoning}
                    </p>
                  </details>
                ) : null}

                {/* Raw model response + token usage — full audit trail */}
                {latestValid.rawAiResponse ? (
                  <details className="mt-3 rounded-xl border border-zinc-200 p-3 text-sm">
                    <summary className="cursor-pointer font-medium text-zinc-700">
                      {t("workspace.showRawResponse")}
                    </summary>
                    <pre className="ltr-token mt-2 max-h-64 overflow-auto whitespace-pre-wrap break-all text-xs text-zinc-500">
                      {latestValid.rawAiResponse}
                    </pre>
                  </details>
                ) : null}
                <div className="mt-3 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-zinc-400 ltr-token">
                  <span>
                    {t("workspace.inputTokens")}: {formatNumber(latestValid.inputTokens, lang)}
                  </span>
                  <span>
                    {t("workspace.outputTokens")}: {formatNumber(latestValid.outputTokens, lang)}
                  </span>
                  <span>
                    {t("workspace.estimatedCost")}: {formatCost(latestValid.estimatedCost, lang)}
                  </span>
                  <span title={formatDateTime(latestValid.createdAt, lang)}>
                    {timeAgo(latestValid.createdAt, lang)}
                  </span>
                </div>

                <div className="mt-4 flex flex-wrap gap-2 border-t border-zinc-100 pt-4">
                  <Button onClick={() => openReview("accept")}>
                    <Check size={15} /> {t("workspace.accept")}
                  </Button>
                  <Button variant="secondary" onClick={() => openReview("override")}>
                    <Pencil size={14} /> {t("workspace.override")}
                  </Button>
                  <span className="flex-1" />
                  <span className="self-center text-[11px] text-zinc-400">
                    {latestValid.modelName} · T=
                    {formatNumber(latestValid.temperature, lang, { maximumFractionDigits: 2 })} ·{" "}
                    {formatLatency(latestValid.latencyMs, lang)} · {formatCost(latestValid.estimatedCost, lang)}
                  </span>
                </div>
              </>
            )}
          </Card>

          {/* Run AI grading controls */}
          <Card>
            <CardHeader
              title={t("gradingDialog.title")}
              subtitle={t("gradingDialog.blindGradingNote")}
              action={<Bot size={16} className="text-primary-500" />}
            />
            <div className="grid gap-3 sm:grid-cols-3">
              <Field label={t("gradingDialog.provider")}>
                <Select value={provider} onChange={(e) => setProvider(e.target.value)}>
                  <option value="">{t("providers.any")}</option>
                  <option value="claude">{t("providers.claude")}</option>
                  <option value="gemini">{t("providers.gemini")}</option>
                  <option value="qwen">{t("providers.qwen")}</option>
                </Select>
              </Field>
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
              <Field label={t("gradingDialog.runs")} hint={t("gradingDialog.runsHint")}>
                <Input
                  type="number"
                  min={1}
                  max={10}
                  value={runCount}
                  onChange={(e) => setRunCount(Math.min(10, Math.max(1, Number(e.target.value))))}
                />
              </Field>
            </div>
            <div className="mt-4 flex items-center justify-between gap-3">
              <p className="text-[11px] text-zinc-400">{t("gradingDialog.estimatedCostNote")}</p>
              <Button onClick={() => runMut.mutate()} loading={runMut.isPending}>
                <Play size={15} /> {t("gradingDialog.startGrading")}
              </Button>
            </div>
            {runMut.isPending ? (
              <p className="mt-2 animate-pulse text-center text-xs font-medium text-primary-600">
                {t("gradingDialog.progressLabel")}
              </p>
            ) : null}
          </Card>

          {/* Run history timeline */}
          <Card>
            <CardHeader title={t("workspace.runHistory")} subtitle={t("runs.subtitle")} />
            {runsQ.isLoading ? (
              <LoadingBlock />
            ) : !(runsQ.data ?? []).length ? (
              <p className="py-6 text-center text-sm text-zinc-400">{t("states.noAiResult")}</p>
            ) : (
              <ul className="space-y-2.5">
                {[...(runsQ.data ?? [])]
                  .sort((x, y) => +new Date(y.createdAt) - +new Date(x.createdAt))
                  .map((r) => (
                    <li key={r.id}>
                      <RunRow run={r} />
                    </li>
                  ))}
              </ul>
            )}
          </Card>

          {/* Review history (append-only) */}
          <Card>
            <CardHeader title={t("workspace.reviewHistory")} />
            {reviewsQ.isLoading ? (
              <LoadingBlock />
            ) : !(reviewsQ.data ?? []).length ? (
              <p className="py-6 text-center text-sm text-zinc-400">{t("workspace.noReviewHistory")}</p>
            ) : (
              <ul className="divide-y divide-zinc-100">
                {(reviewsQ.data ?? []).map((rv: TeacherReviewDto) => (
                  <li key={rv.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2.5 text-xs">
                    <Badge tone={rv.action === "Accept" ? "green" : "amber"}>
                      {rv.action === "Accept" ? t("workspace.acceptedBy") : t("workspace.overriddenBy")}
                    </Badge>
                    <strong className="tabular-nums">{formatScore(rv.newScore, lang)}</strong>
                    <span className="text-zinc-400">
                      ({t("workspace.aiScore")} {formatScore(rv.oldAiScore, lang)})
                    </span>
                    {rv.note ? <span className="min-w-0 flex-1 truncate text-zinc-500" title={rv.note}>{rv.note}</span> : null}
                    <span className="flex-1" />
                    <span className="text-zinc-400" title={formatDateTime(rv.reviewedAt, lang)}>
                      {timeAgo(rv.reviewedAt, lang)}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </Card>
        </div>
      </div>

      {/* nav footer: prev / queue / next — keeps student order stable */}
      <div className="mt-6 flex items-center justify-between gap-3">
        <Button
          variant="ghost"
          size="sm"
          disabled={!prevAnswer}
          onClick={() => prevAnswer && navigate(`/grading/workspace/${prevAnswer.id}`)}
        >
          ← {t("workspace.prevAnswer")}
        </Button>
        <Link
          to="/grading/queue"
          className="text-xs font-medium text-zinc-500 hover:text-primary-600"
        >
          {t("queue.title")}
        </Link>
        <Button
          variant="ghost"
          size="sm"
          disabled={!nextAnswer}
          onClick={() => nextAnswer && navigate(`/grading/workspace/${nextAnswer.id}`)}
        >
          {t("workspace.nextAnswer")} →
        </Button>
      </div>

      {/* Accept / Override dialog */}
      <Dialog
        open={!!reviewMode}
        onClose={() => setReviewMode(null)}
        title={reviewMode === "accept" ? t("workspace.acceptTitle") : t("workspace.overrideTitle")}
        description={t("gradingDialog.blindGradingNote")}
      >
        {reviewMode ? (
          <form onSubmit={handleReviewSubmit}>
            {/* Identity guard — backend rejects reviews without a valid GUID header */}
            {!identityReady || identityError ? (
              <div className="mb-4 rounded-xl border border-amber-200 bg-amber-50 p-3">
                <Field label={`${t("settings.userId")} (${t("common.required")})`} htmlFor="rv-uid">
                  <Input
                    id="rv-uid"
                    value={identityInput}
                    onChange={(e) => {
                      setIdentityInput(e.target.value);
                      setIdentityError(null);
                    }}
                    placeholder="00000000-0000-0000-0000-000000000000"
                    autoComplete="off"
                    dir="ltr"
                    required
                  />
                </Field>
                {identityError ? (
                  <p className="mt-1.5 text-xs text-red-600" role="alert">
                    {identityError}
                  </p>
                ) : null}
              </div>
            ) : null}

            {reviewMode === "override" ? (
              <Field label={t("students.teacherScore")} required htmlFor="ov-score">
                <Input
                  id="ov-score"
                  type="number"
                  min={0}
                  max={question?.maxScore ?? 100}
                  step={0.25}
                  value={overrideScore}
                  onChange={(e) => setOverrideScore(Number(e.target.value))}
                  required
                />
              </Field>
            ) : (
              <p className="rounded-xl bg-emerald-50 p-3 text-sm text-emerald-800">
                {t("workspace.acceptConfirm")}{" "}
                <strong className="tabular-nums">{formatScore(latestValid?.aiScore, lang)}</strong>
              </p>
            )}
            <div className="mt-3">
              <Field label={`${t("reviews.note")} (${t("common.optional")})`} htmlFor="rv-note">
                <Input id="rv-note" value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} />
              </Field>
            </div>
            <div className="mt-5 flex justify-end gap-2">
              <Button type="button" variant="secondary" onClick={() => setReviewMode(null)}>
                {t("common.cancel")}
              </Button>
              <Button type="submit" loading={reviewMut.isPending}>
                {t("common.confirm")}
              </Button>
            </div>
          </form>
        ) : null}
      </Dialog>
    </>
  );
}

function RunRow({ run }: { run: GradingRun }) {
  const { t } = useTranslation();
  const lang = currentLang();
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-xl border border-zinc-100 bg-zinc-50/60 px-3 py-2 text-xs">
      <Badge tone={run.isValid ? "green" : "red"}>{formatScore(run.aiScore, lang)}</Badge>
      <span className="font-medium text-zinc-600">{run.modelName}</span>
      <span className="text-zinc-400">{run.provider}</span>
      <span className="text-zinc-400">
        T={formatNumber(run.temperature, lang, { maximumFractionDigits: 2 })}
      </span>
      <span className="text-zinc-400">{formatLatency(run.latencyMs, lang)}</span>
      <span className="text-zinc-400">{formatCost(run.estimatedCost, lang)}</span>
      <span className="text-zinc-400" title={formatDateTime(run.createdAt, lang)}>
        {timeAgo(run.createdAt, lang)}
      </span>
      <span className="flex-1" />
      {!run.isValid && run.error ? (
        <span className="w-full truncate text-red-500" title={run.error}>
          {run.error}
        </span>
      ) : run.teacherScoreSnapshot != null ? (
        <Badge tone="blue">
          {t("workspace.reviewedAs")} {formatScore(run.teacherScoreSnapshot, lang)}
        </Badge>
      ) : null}
    </div>
  );
}