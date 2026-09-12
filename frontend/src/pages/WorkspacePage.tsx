import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { clsx } from "clsx";
import { useTranslation } from "react-i18next";
import {
  Bot,
  Check,
  FileText,
  Layers,
  Pencil,
  Play,
  Plus,
  ShieldCheck,
  Trash2,
} from "lucide-react";
import {
  getAnswer,
  setTeacherScore as apiSetTeacherScore,
  listAnswersByQuestion,
} from "../api/answers";
import { apiErrorMessage, fetchAuthenticatedFile } from "../api/client";
import { getQuestion, getActiveRubric, createRubric } from "../api/questions";
import type { RubricCriterionDto } from "../types/models";
import {
  runGrading,
  listRunsForAnswer,
  listReviewsForAnswer,
  acceptRun,
  overrideRun,
} from "../api/grading";
import type { TeacherReviewDto } from "../types/models";
import type { GradingRun } from "../types/models";
import { parseCriteriaScores } from "../types/models";
import {
  PageHeader,
  Card,
  CardHeader,
  Badge,
  Input,
  Textarea,
  Field,
  Button,
  LoadingBlock,
  ErrorState,
  Dialog,
  friendlyError,
} from "../components/ui";
import { AnswerStatusBadge } from "../components/common";
import { AuthFileView } from "../components/AuthFileView";
import { formatCost, formatLatency, formatNumber, formatScore, formatDateTime, timeAgo } from "../utils/format";
import { currentLang } from "../hooks/useLang";
import { useToast } from "../hooks/useToast";
import { getAuthUser } from "../api/auth";
import { hasRole } from "../utils/roles";

export default function WorkspacePage() {
  const { answerId = "" } = useParams();
  const { t } = useTranslation();
  const lang = currentLang();
  const qc = useQueryClient();
  const toast = useToast();
  const navigate = useNavigate();

  // POST /grading/run is Teacher-only; accept/override reviews are Teacher+Corrector.
  const canRunGrading = hasRole(getAuthUser(), "Teacher");
  const canReview = hasRole(getAuthUser(), "Teacher", "Corrector");

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
  const [temperature, setTemperature] = useState(0);
  const [runCount, setRunCount] = useState(1);

  // Run-history selection: when null the workspace shows the LATEST valid run;
  // clicking a run in "تاریخچه اجراها" pins that specific run instead.
  const [activeRunId, setActiveRunId] = useState<string | null>(null);

  // review dialog state
  const [reviewMode, setReviewMode] = useState<"accept" | "override" | null>(null);
  const [overrideScore, setOverrideScore] = useState<number>(0);
  const [note, setNote] = useState("");

  // rubric editor state (Teacher-only card) — editing rows for the active rubric
  const [rubricRows, setRubricRows] = useState<RubricCriterionDto[] | null>(null);
  function rubricRowKey(questionNumber: number): string {
    return `q${questionNumber}${String.fromCharCode(97 + (rubricRows?.length ?? 0))}`;
  }

  const latestValid = useMemo(
    () =>
      (runsQ.data ?? [])
        .filter((r) => r.isValid)
        .sort((x, y) => +new Date(y.createdAt) - +new Date(x.createdAt))[0],
    [runsQ.data]
  );

  // The run whose full detail is shown in the "Latest AI score" card +
  // review flow. Prefers the user-selected history item; falls back to the
  // latest valid run (or undefined when there are no runs yet).
  const activeRun = useMemo(() => {
    if (activeRunId) return (runsQ.data ?? []).find((r) => r.id === activeRunId) ?? latestValid;
    return latestValid;
  }, [runsQ.data, activeRunId, latestValid]);

  const runMut = useMutation({
    mutationFn: () =>
      runGrading({
        answerId,
        temperature,
        runs: runCount,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["runs", answerId] });
      qc.invalidateQueries({ queryKey: ["answer", answerId] });
      // A fresh batch was just produced — show the newest run, not a stale pin.
      setActiveRunId(null);
      toast.info(t("states.gradingStarted"));
    },
    onError: (e) => toast.error(friendlyError(apiErrorMessage(e), t)),
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
      if (!activeRun || !reviewMode) return;
      if (reviewMode === "accept") await acceptRun(activeRun.id, note || undefined);
      else await overrideRun(activeRun.id, overrideScore, note || undefined);

      const score = reviewMode === "accept" ? activeRun.aiScore : overrideScore;
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

  /** Reviewer identity is derived server-side from the JWT — no input needed. */
  function openReview(mode: "accept" | "override") {
    if (!activeRun) return;
    setReviewMode(mode);
  }

  // Grading reads the SAVED active rubric rows from the DB. Saving here
  // creates a new version (POST /questions/{id}/rubrics) — the next run uses
  // it; previous versions stay in history.
  const saveRubricMut = useMutation({
    mutationFn: () =>
      createRubric({
        questionId: answer.questionId,
        criteria: (rubricRows ?? [])
          .filter((c) => c.description.trim())
          .map((c, i) => ({
            criterionId: c.criterionId.trim(),
            description: c.description.trim(),
            maxScore: c.maxScore,
            order: i,
          })),
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["rubric", answer.questionId] });
      qc.invalidateQueries({ queryKey: ["question-single", answer.questionId] });
      setRubricRows(null);
      toast.success(t("rubric.versionCreated"));
    },
    onError: (e) => toast.error(friendlyError(apiErrorMessage(e), t)),
  });

  function handleReviewSubmit(e: React.FormEvent) {
    e.preventDefault();
    reviewMut.mutate();
  }

  if (answerQ.isLoading) return <LoadingBlock />;
  if (answerQ.isError)
    return <ErrorState message={friendlyError(answerQ.error, t)} onRetry={() => answerQ.refetch()} />;

  const answer = answerQ.data!;
  const question = questionQ.data;
  const criteriaScores = activeRun ? parseCriteriaScores(activeRun.criteriaScoresJson) : [];

  const rubricSum = (rubricRows ?? []).reduce(
    (s, c) => s + (Number(c.maxScore) || 0),
    0
  );
  const rubricSumMismatch =
    question != null && Math.abs(rubricSum - question.maxScore) > 1e-9;

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
            {answer.contentType ===
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" ? (
              <div className="flex h-40 flex-col items-center justify-center gap-2 text-sm text-zinc-500">
                <FileText size={22} className="text-red-400" />
                <span>{answer.fileName ?? t("viewer.answerScan")}</span>
                {/* Authenticated download — a bare <a href> cannot attach the JWT and gets 401. */}
                <Button
                  size="sm"
                  variant="secondary"
                  onClick={async () => {
                    try {
                      const file = await fetchAuthenticatedFile(
                        `/answers/${answer.id}/image`,
                        answer.fileName ?? "answer"
                      );
                      const a = document.createElement("a");
                      a.href = file.url;
                      a.download = file.fileName;
                      document.body.appendChild(a);
                      a.click();
                      a.remove();
                      setTimeout(() => URL.revokeObjectURL(file.url), 10_000);
                    } catch (e) {
                      toast.error(friendlyError(apiErrorMessage(e), t));
                    }
                  }}
                >
                  {t("common.download")}
                </Button>
              </div>
            ) : (
              /* Authenticated blob fetch — bare img/iframe URLs get 401 (no JWT header). */
              <AuthFileView
                path={`/answers/${answer.id}/image`}
                contentType={answer.contentType ?? null}
                alt={`${t("viewer.answerScan")} — ${answer.studentExternalId}`}
                className={
                  answer.contentType === "application/pdf"
                    ? "h-[560px] w-full"
                    : "max-h-[560px] w-full object-contain"
                }
              />
            )}
          </div>
          {question ? (
            <details className="mt-3 rounded-xl border border-zinc-200 p-3 text-sm">
              <summary className="cursor-pointer font-medium text-zinc-700">
                {t("questions.text")}
              </summary>
              <p className="mt-2 leading-relaxed text-zinc-600">{question.text}</p>
              {rubricQ.data && !canRunGrading ? (
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

        {/* ── Rubric editor (Teacher-only) ─────────────────────────────────
            Prefilled from the active rubric rows; saving creates a new
            version and the next AI run grades against it. Non-teachers keep
            the read-only list in the details block above. */}
        {canRunGrading && question ? (
          <Card>
            <CardHeader
              title={t("rubric.title")}
              subtitle={
                rubricQ.data
                  ? t("rubric.versionN", { version: rubricQ.data.version })
                  : t("rubric.noRubricHint")
              }
              action={<Layers size={16} className="text-primary-500" />}
            />
            {rubricRows === null ? (
              <>
                {rubricQ.data ? (
                  <ul className="mb-3 space-y-1 rounded-lg bg-zinc-50 p-2.5 text-xs text-zinc-600">
                    {rubricQ.data.criteria.map((c) => (
                      <li key={c.criterionId} className="flex justify-between gap-2">
                        <span className="line-clamp-1">{c.description}</span>
                        <span className="shrink-0 font-medium tabular-nums">
                          {formatScore(c.maxScore, lang)}
                        </span>
                      </li>
                    ))}
                  </ul>
                ) : (
                  <p className="py-4 text-center text-sm text-zinc-400">{t("rubric.noRubric")}</p>
                )}
                <div className="flex items-center gap-2">
                  <Button
                    size="sm"
                    variant="secondary"
                    onClick={() =>
                      setRubricRows(
                        rubricQ.data
                          ? rubricQ.data.criteria.map((c) => ({ ...c }))
                          : [
                              {
                                criterionId: rubricRowKey(question.number),
                                description: "",
                                maxScore: 1,
                                order: 0,
                              },
                            ]
                      )
                    }
                  >
                    <Pencil size={13} /> {t("rubric.editRubric")}
                  </Button>
                  <p className="text-[11px] text-zinc-400">{t("rubric.workspaceHint")}</p>
                </div>
              </>
            ) : (
              <div className="space-y-2">
                {(rubricRows ?? []).map((c, i) => (
                  <div key={i} className="flex items-start gap-2">
                    <div className="flex-1">
                      <Textarea
                        rows={2}
                        value={c.description}
                        placeholder={t("rubric.criterionDescription")}
                        onChange={(e) =>
                          setRubricRows(
                            rubricRows.map((r, j) =>
                              j === i ? { ...r, description: e.target.value } : r
                            )
                          )
                        }
                      />
                    </div>
                    <div className="w-20">
                      <Input
                        type="number"
                        min={0}
                        step={0.5}
                        value={c.maxScore}
                        aria-label={t("questions.maxScore")}
                        onChange={(e) =>
                          setRubricRows(
                            rubricRows.map((r, j) =>
                              j === i ? { ...r, maxScore: Number(e.target.value) } : r
                            )
                          )
                        }
                      />
                    </div>
                    <button
                      type="button"
                      onClick={() => setRubricRows(rubricRows.filter((_, j) => j !== i))}
                      className="mt-2 rounded-lg p-1.5 text-zinc-400 transition hover:bg-red-50 hover:text-red-600"
                      aria-label={t("rubric.removeCriterion")}
                    >
                      <Trash2 size={15} />
                    </button>
                  </div>
                ))}
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={() =>
                    setRubricRows([
                      ...rubricRows,
                      {
                        criterionId: rubricRowKey(question.number),
                        description: "",
                        maxScore: 1,
                        order: rubricRows.length,
                      },
                    ])
                  }
                >
                  <Plus size={14} /> {t("rubric.addCriterion")}
                </Button>
                {rubricSumMismatch ? (
                  <p className="rounded-xl bg-amber-50 p-2.5 text-xs text-amber-800">
                    {t("rubric.totalScoreMismatch", {
                      sum: formatScore(rubricSum, lang),
                      max: formatScore(question.maxScore, lang),
                    })}
                  </p>
                ) : null}
                <div className="flex items-center justify-end gap-2 pt-1">
                  <Button variant="secondary" size="sm" onClick={() => setRubricRows(null)}>
                    {t("common.cancel")}
                  </Button>
                  <Button
                    size="sm"
                    onClick={() => saveRubricMut.mutate()}
                    loading={saveRubricMut.isPending}
                    disabled={saveRubricMut.isPending || !(rubricRows ?? []).some((c) => c.description.trim())}
                  >
                    {t("rubric.saveNewVersion")}
                  </Button>
                </div>
              </div>
            )}
          </Card>
        ) : null}

        {/* ── AI result + review pane ── */}
        <div className="space-y-4">
          {/* Latest AI score card */}
          <Card>
            <CardHeader
              title={t("workspace.latestAiResult")}
              subtitle={
                activeRun
                  ? `${activeRun.modelName} · ${formatDateTime(activeRun.createdAt, lang)}`
                  : undefined
              }
              action={
                activeRun ? (
                  <Badge tone={activeRun.isValid ? "green" : "red"}>
                    <ShieldCheck size={11} />
                    {activeRun.isValid ? t("grading.valid") : t("status.error")}
                  </Badge>
                ) : null
              }
            />
            {!activeRun ? (
              <p className="py-6 text-center text-sm text-zinc-400">{t("states.noAiResult")}</p>
            ) : (
              <>
                <div className="mb-3 grid grid-cols-3 gap-3">
                  <div className="rounded-xl bg-primary-50 p-3 text-center">
                    <p className="text-[11px] font-medium text-primary-700">AI</p>
                    <p className="mt-1 text-2xl font-bold tabular-nums text-primary-800">
                      {formatScore(activeRun.aiScore, lang)}
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
                        ? formatScore(Math.abs(activeRun.aiScore - answer.teacherScore), lang)
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

                {activeRun.reasoning ? (
                  <details className="mt-3 rounded-xl border border-zinc-200 p-3 text-sm">
                    <summary className="cursor-pointer font-medium text-zinc-700">
                      {t("workspace.reasoning")}
                    </summary>
                    <p className="ltr-token mt-2 whitespace-pre-wrap leading-relaxed text-zinc-600">
                      {activeRun.reasoning}
                    </p>
                  </details>
                ) : null}

                {/* Raw model response + token usage — full audit trail */}
                {activeRun.rawAiResponse ? (
                  <details className="mt-3 rounded-xl border border-zinc-200 p-3 text-sm">
                    <summary className="cursor-pointer font-medium text-zinc-700">
                      {t("workspace.showRawResponse")}
                    </summary>
                    <pre className="ltr-token mt-2 max-h-64 overflow-auto whitespace-pre-wrap break-all text-xs text-zinc-500">
                      {activeRun.rawAiResponse}
                    </pre>
                  </details>
                ) : null}
                <div className="mt-3 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-zinc-400 ltr-token">
                  <span>
                    {t("workspace.inputTokens")}: {formatNumber(activeRun.inputTokens, lang)}
                  </span>
                  <span>
                    {t("workspace.outputTokens")}: {formatNumber(activeRun.outputTokens, lang)}
                  </span>
                  <span>
                    {t("workspace.estimatedCost")}: {formatCost(activeRun.estimatedCost, lang)}
                  </span>
                  <span title={formatDateTime(activeRun.createdAt, lang)}>
                    {timeAgo(activeRun.createdAt, lang)}
                  </span>
                </div>

                <div className="mt-4 flex flex-wrap gap-2 border-t border-zinc-100 pt-4">
                  {canReview ? (
                    <>
                      <Button onClick={() => openReview("accept")}>
                        <Check size={15} /> {t("workspace.accept")}
                      </Button>
                      <Button variant="secondary" onClick={() => openReview("override")}>
                        <Pencil size={14} /> {t("workspace.override")}
                      </Button>
                    </>
                  ) : null}
                  <span className="flex-1" />
                  <span className="self-center text-[11px] text-zinc-400">
                    {activeRun.modelName} · T=
                    {formatNumber(activeRun.temperature, lang, { maximumFractionDigits: 2 })} ·{" "}
                    {formatLatency(activeRun.latencyMs, lang)} · {formatCost(activeRun.estimatedCost, lang)}
                  </span>
                </div>
              </>
            )}
          </Card>

          {/* Run AI grading controls (Teacher-only — mirrors [Authorize(Roles=Teacher)] on POST /grading/run) */}
          {canRunGrading ? (
          <Card>
            <CardHeader
              title={t("gradingDialog.title")}
              subtitle={t("gradingDialog.blindGradingNote")}
              action={<Bot size={16} className="text-primary-500" />}
            />
            <div className="grid gap-3 sm:grid-cols-2">
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
              <p className="text-[11px] text-zinc-400">{t("gradingDialog.usesSavedRubric")}</p>
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
          ) : null}

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
                      <RunRow
                        run={r}
                        selected={activeRunId === r.id}
                        onClick={() =>
                          setActiveRunId((cur) => (cur === r.id ? null : r.id))
                        }
                      />
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
                <strong className="tabular-nums">{formatScore(activeRun?.aiScore, lang)}</strong>
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

const PROVIDER_KEYS: Record<string, string> = {
  // Current slots.
  glm: "providers.glm",
  gpt: "providers.gpt",
  // Legacy labels — old DB rows were stored as "qwen" (the GLM slot's former
  // internal name) or "claude"; map them so history stays readable.
  qwen: "providers.glm",
  claude: "providers.claude",
};

function RunRow({
  run,
  selected = false,
  onClick,
}: {
  run: GradingRun;
  selected?: boolean;
  onClick?: () => void;
}) {
  const { t } = useTranslation();
  const lang = currentLang();
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={!onClick}
      aria-pressed={selected}
      title={onClick ? t("workspace.runHistory") : undefined}
      className={clsx(
        "flex w-full flex-wrap items-center gap-x-3 gap-y-1 rounded-xl border px-3 py-2 text-start text-xs transition-colors",
        onClick && "cursor-pointer",
        selected
          ? "border-primary-300 bg-primary-50/70 shadow-sm ring-1 ring-primary-200"
          : "border-zinc-100 bg-zinc-50/60 hover:bg-zinc-100/70"
      )}
    >
      <Badge tone={run.isValid ? "green" : "red"}>{formatScore(run.aiScore, lang)}</Badge>
      <span className="font-medium text-zinc-600">{run.modelName}</span>
      <span className="text-zinc-400">{t(PROVIDER_KEYS[run.provider] ?? run.provider)}</span>
      <span className="text-zinc-400">
        T={formatNumber(run.temperature, lang, { maximumFractionDigits: 2 })}
      </span>
      <span className="text-zinc-400">{formatLatency(run.latencyMs, lang)}</span>
      <span className="text-zinc-400">{formatCost(run.estimatedCost, lang)}</span>
      <span className="text-zinc-400" title={formatDateTime(run.createdAt, lang)}>
        {timeAgo(run.createdAt, lang)}
      </span>
      <span className="flex-1" />
      {run.teacherScoreSnapshot != null ? (
        <Badge tone="blue">
          {t("workspace.reviewedAs")} {formatScore(run.teacherScoreSnapshot, lang)}
        </Badge>
      ) : null}
      {!run.isValid && run.error ? (
        <span className="w-full truncate text-red-500" title={run.error}>
          {run.error}
        </span>
      ) : null}
    </button>
  );
}