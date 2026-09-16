import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { CheckCheck, FileStack, ShieldAlert, Sparkles } from "lucide-react";
import { listExams } from "../api/exams";
import { listQuestionsByExam } from "../api/questions";
import { listStudents } from "../api/students";
import {
  uploadBulkBatch,
  getBulkBatch,
  updateBulkPage,
  applyBulkBatch,
} from "../api/bulk";
import {
  PageHeader,
  Card,
  CardHeader,
  Badge,
  Button,
  Select,
  Field,
  ConfirmDialog,
  LoadingBlock,
  ErrorState,
  EmptyState,
  friendlyError,
} from "../components/ui";
import { AuthFileView } from "../components/AuthFileView";
import { apiErrorMessage } from "../api/client";
import { useToast } from "../hooks/useToast";
import { getAuthUser } from "../api/auth";
import { hasRole } from "../utils/roles";
import { toFaDigits } from "../utils/format";
import type { BulkBatchDto, BulkPageDto, StudentDto } from "../types/models";

/** Status chip color per review status — the safety signal on every card. */
function StatusBadge({ status }: { status: string }) {
  const { t } = useTranslation();
  const tone =
    status === "auto" || status === "confirmed"
      ? "green"
      : status === "needs_review"
        ? "amber"
        : status === "skipped"
          ? "zinc"
          : "red";
  const key =
    status === "needs_review"
      ? "bulk.pageStatus.needs_review"
      : `bulk.pageStatus.${status}`;
  return <Badge tone={tone}>{t(key)}</Badge>;
}

function num(n: number, lang: string): string {
  return lang === "fa" ? toFaDigits(String(n)) : String(n);
}

/** One page thumbnail + its fix actions (assign / confirm / skip / clear). */
function PageCard({
  page,
  batchId,
  students,
  onChanged,
  t,
  lang,
}: {
  page: BulkPageDto;
  batchId: string;
  students: StudentDto[];
  onChanged: () => void;
  t: (k: string, o?: Record<string, unknown>) => string;
  lang: string;
}) {
  const toast = useToast();
  const [assigning, setAssigning] = useState(false);

  const patch = useMutation({
    mutationFn: (body: {
      studentId?: string | null;
      confirm?: boolean;
      skip?: boolean;
      clear?: boolean;
    }) => updateBulkPage(batchId, page.id, body),
    onSuccess: onChanged,
    onError: (e) => toast.error(friendlyError(apiErrorMessage(e), t)),
  });

  const frozen = page.reviewStatus === "skipped";

  return (
    <div className="w-40 shrink-0 rounded-xl border border-zinc-200 bg-white p-1.5">
      <div className="relative">
        <AuthFileView
          path={`/answers/bulk/pages/${page.pageArtifactId}/image`}
          contentType="image/png"
          alt={t("bulk.pageAlt", { n: page.pageNumber })}
          className="h-40 w-full rounded-lg object-cover object-top"
        />
        <span className="absolute left-1 top-1 rounded bg-zinc-900/70 px-1 text-[10px] font-medium text-white">
          {t("bulk.pageShort", { n: num(page.pageNumber, lang) })}
        </span>
      </div>

      <div className="mt-1.5 space-y-1">
        <div className="flex items-center justify-between gap-1">
          <StatusBadge status={page.reviewStatus} />
          {page.rawOcrId ? (
            <span
              className="truncate font-mono text-[11px] text-zinc-600"
              title={`OCR ${Math.round(page.ocrConfidence * 100)}%`}
            >
              {page.rawOcrId} · {num(Math.round(page.ocrConfidence * 100), lang)}٪
            </span>
          ) : (
            <span className="text-[11px] text-zinc-400">{t("bulk.noId")}</span>
          )}
        </div>

        {assigning ? (
          <Select
            autoFocus
            className="!py-1 text-xs"
            value=""
            onChange={(e) => {
              if (e.target.value) patch.mutate({ studentId: e.target.value });
              setAssigning(false);
            }}
            onBlur={() => setAssigning(false)}
          >
            <option value="">{t("bulk.pickStudent")}</option>
            {students.map((s) => (
              <option key={s.id} value={s.id}>
                {s.displayName ? `${s.displayName} (${s.externalId})` : s.externalId}
              </option>
            ))}
          </Select>
        ) : (
          <div className="flex flex-wrap gap-1">
            <Button size="sm" variant="secondary" onClick={() => setAssigning(true)}>
              {page.matchedStudentId ? t("bulk.reassign") : t("bulk.assign")}
            </Button>
            {!frozen && page.reviewStatus !== "confirmed" ? (
              <Button size="sm" variant="ghost" onClick={() => patch.mutate({ confirm: true })}>
                {t("common.confirm")}
              </Button>
            ) : null}
            {!frozen ? (
              <Button size="sm" variant="ghost" onClick={() => patch.mutate({ skip: true })}>
                {t("bulk.skip")}
              </Button>
            ) : (
              <Button size="sm" variant="ghost" onClick={() => patch.mutate({ clear: true })}>
                {t("bulk.restore")}
              </Button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

/** One detected stack (consecutive pages with the same student id). */
function StackCard({
  label,
  flagged,
  pages,
  batchId,
  students,
  onChanged,
  t,
  lang,
}: {
  label: string;
  flagged: boolean;
  pages: BulkPageDto[];
  batchId: string;
  students: StudentDto[];
  onChanged: () => void;
  t: (k: string, o?: Record<string, unknown>) => string;
  lang: string;
}) {
  const toast = useToast();

  const bulk = useMutation({
    mutationFn: async (body: { confirm?: boolean; skip?: boolean }) => {
      await Promise.all(pages.map((p) => updateBulkPage(batchId, p.id, body)));
    },
    onSuccess: onChanged,
    onError: (e) => toast.error(friendlyError(apiErrorMessage(e), t)),
  });

  const reviewable = pages.filter((p) => p.reviewStatus === "auto" || p.reviewStatus === "needs_review");

  return (
    <Card className="flex flex-col gap-3">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="truncate text-sm font-semibold text-zinc-900">{label}</p>
          <p className="mt-0.5 text-xs text-zinc-500">
            {t("bulk.pagesCount", { n: num(pages.length, lang) })}
          </p>
        </div>
        {flagged ? <Badge tone="amber">{t("bulk.flaggedStack")}</Badge> : null}
      </div>

      <div className="flex gap-2 overflow-x-auto pb-1">
        {pages.map((p) => (
          <PageCard
            key={p.id}
            page={p}
            batchId={batchId}
            students={students}
            onChanged={onChanged}
            t={t}
            lang={lang}
          />
        ))}
      </div>

      {reviewable.length > 0 ? (
        <div className="flex gap-2 border-t border-zinc-100 pt-2">
          <Button
            size="sm"
            variant="secondary"
            loading={bulk.isPending}
            onClick={() => bulk.mutate({ confirm: true })}
          >
            <CheckCheck size={14} />
            {t("bulk.confirmAll", { n: num(reviewable.length, lang) })}
          </Button>
        </div>
      ) : null}
    </Card>
  );
}

export default function BulkUploadPage() {
  const { t, i18n } = useTranslation();
  const lang = i18n.language?.startsWith("fa") ? "fa" : "en";
  const toast = useToast();
  const qc = useQueryClient();
  const user = getAuthUser();
  const isTeacher = hasRole(user, "Teacher");

  const examsQ = useQuery({ queryKey: ["exams"], queryFn: listExams });
  const [examId, setExamId] = useState("");
  const questionsQ = useQuery({
    queryKey: ["questions", examId],
    queryFn: () => listQuestionsByExam(examId),
    enabled: !!examId,
  });
  const [questionId, setQuestionId] = useState("");
  const studentsQ = useQuery({ queryKey: ["students"], queryFn: listStudents });

  const [file, setFile] = useState<File | null>(null);
  const [batchId, setBatchId] = useState("");
  const [confirmApply, setConfirmApply] = useState(false);

  // Poll while the split runs; stop once reviewable/applied/failed.
  const batchQ = useQuery({
    queryKey: ["bulk-batch", batchId],
    queryFn: () => getBulkBatch(batchId),
    enabled: !!batchId && isTeacher,
    refetchInterval: (q) => (q.state.data?.status === "splitting" ? 2500 : false),
  });
  const batch: BulkBatchDto | undefined = batchQ.data;

  const uploadMut = useMutation({
    mutationFn: () => uploadBulkBatch(questionId, file as File),
    onSuccess: (res) => {
      setFile(null);
      setBatchId(res.id);
      toast.success(t("bulk.uploaded"));
    },
    onError: (e) => toast.error(friendlyError(apiErrorMessage(e), t)),
  });

  const applyMut = useMutation({
    mutationFn: () => applyBulkBatch(batchId),
    onSuccess: (res) => {
      setConfirmApply(false);
      toast.success(
        t("bulk.appliedSummary", {
          students: num(res.students, lang),
          pages: num(res.pagesApplied, lang),
          skipped: num(res.pagesSkipped, lang),
          unassigned: num(res.pagesUnassigned, lang),
        })
      );
      qc.invalidateQueries({ queryKey: ["bulk-batch", batchId] });
      qc.invalidateQueries({ queryKey: ["answers", questionId] });
    },
    onError: (e) => {
      setConfirmApply(false);
      toast.error(friendlyError(apiErrorMessage(e), t));
    },
  });

  // Group pages into stacks: assigned (per student), unknown-ocr-id, unmatched.
  const stacks = useMemo(() => {
    const map = new Map<string, BulkPageDto[]>();
    for (const p of batch?.pages ?? []) {
      const key = p.matchedStudentId
        ? `id:${p.matchedStudentId}`
        : p.rawOcrId
          ? `ocr:${p.rawOcrId}`
          : "unmatched";
      const list = map.get(key) ?? [];
      list.push(p);
      map.set(key, list);
    }
    return [...map.entries()]
      .map(([key, pages]) => {
        pages.sort((a, b) => a.pageNumber - b.pageNumber);
        const first = pages[0];
        const label =
          key === "unmatched"
            ? t("bulk.unmatchedBucket")
            : key.startsWith("ocr:")
              ? t("bulk.unknownIdLabel", { id: key.slice(4) })
              : first.matchedStudentDisplayName ||
                first.matchedStudentExternalId ||
                key.slice(3);
        const flagged =
          key.startsWith("ocr:") &&
          pages.some((p) => p.reviewStatus === "needs_review");
        return { key, pages, label, flagged };
      })
      .sort((a, b) => a.pages[0].pageNumber - b.pages[0].pageNumber);
  }, [batch?.pages, t]);

  const needsReviewCount = batch?.pages.filter(
    (p) => p.reviewStatus === "needs_review" || p.reviewStatus === "unmatched"
  ).length ?? 0;

  if (!isTeacher) return <ErrorState message={t("errors.forbidden")} />;

  return (
    <div>
      <PageHeader
        title={t("bulk.title")}
        subtitle={t("bulk.subtitle")}
        action={<FileStack className="h-6 w-6 text-primary-500" />}
      />

      <Card className="mb-6">
        <CardHeader title={t("bulk.uploadTitle")} subtitle={t("bulk.uploadSubtitle")} />
        <div className="flex flex-wrap items-end gap-3">
          <Field label={t("common.exam")}>
            <Select
              value={examId}
              onChange={(e) => {
                setExamId(e.target.value);
                setQuestionId("");
              }}
              className="min-w-48"
            >
              <option value="">{t("exams.selectExam")}</option>
              {(examsQ.data ?? []).map((e) => (
                <option key={e.id} value={e.id}>{e.name}</option>
              ))}
            </Select>
          </Field>
          <Field label={t("common.question")}>
            <Select
              value={questionId}
              onChange={(e) => setQuestionId(e.target.value)}
              className="min-w-32"
              disabled={!examId}
            >
              <option value="">{t("questions.pickQuestion")}</option>
              {(questionsQ.data ?? []).map((q) => (
                <option key={q.id} value={q.id}>
                  {t("questions.questionN", { n: q.number })}
                </option>
              ))}
            </Select>
          </Field>
          <Field label={t("bulk.fileLabel")} hint={t("bulk.fileHint")}>
            <input
              type="file"
              accept=".pdf,.png,.jpg,.jpeg,application/pdf,image/png,image/jpeg"
              onChange={(e) => setFile(e.target.files?.[0] ?? null)}
              className="text-sm text-zinc-600"
            />
          </Field>
          <Button
            loading={uploadMut.isPending}
            disabled={!file || !questionId}
            onClick={() => uploadMut.mutate()}
          >
            <Sparkles size={16} />
            {t("bulk.uploadCta")}
          </Button>
        </div>
      </Card>

      {batchId ? (
        batchQ.isLoading ? (
          <LoadingBlock />
        ) : batchQ.isError ? (
          <ErrorState
            message={friendlyError(apiErrorMessage(batchQ.error), t)}
            onRetry={() => batchQ.refetch()}
          />
        ) : batch ? (
          <Card>
            <CardHeader
              title={batch.sourceFileName || t("bulk.batchTitle")}
              subtitle={`${t("common.status")}: ${t(`bulk.batchStatus.${batch.status}`)} · ${t("bulk.pagesCount", { n: num(batch.totalPages, lang) })}`}
              action={
                batch.status === "ready_for_review" ? (
                  <Button onClick={() => setConfirmApply(true)}>
                    <CheckCheck size={16} />
                    {t("bulk.applyCta")}
                  </Button>
                ) : null
              }
            />

            {batch.status === "splitting" ? (
              <LoadingBlock label={t("bulk.splittingHint")} />
            ) : null}

            {batch.status === "failed" ? (
              <ErrorState message={batch.error || t("bulk.failedHint")} onRetry={() => batchQ.refetch()} />
            ) : null}

            {batch.status === "applied" ? (
              <p className="rounded-xl border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
                {t("bulk.appliedDone")}
              </p>
            ) : null}

            {batch.status === "ready_for_review" ? (
              needsReviewCount > 0 ? (
                <p className="mb-4 flex items-center gap-2 rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
                  <ShieldAlert size={16} className="shrink-0" />
                  {t("bulk.reviewBanner", { n: num(needsReviewCount, lang) })}
                </p>
              ) : null
            ) : null}

            {batch.status === "ready_for_review" || batch.status === "applied" ? (
              batch.pages.length === 0 ? (
                <EmptyState title={t("bulk.noPages")} />
              ) : (
                <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
                  {stacks.map((s) => (
                    <StackCard
                      key={s.key}
                      label={s.label}
                      flagged={s.flagged}
                      pages={s.pages}
                      batchId={batchId}
                      students={studentsQ.data ?? []}
                      onChanged={() => qc.invalidateQueries({ queryKey: ["bulk-batch", batchId] })}
                      t={t}
                      lang={lang}
                    />
                  ))}
                </div>
              )
            ) : null}
          </Card>
        ) : null
      ) : null}

      <ConfirmDialog
        open={confirmApply}
        onClose={() => setConfirmApply(false)}
        onConfirm={() => applyMut.mutate()}
        loading={applyMut.isPending}
        title={t("bulk.applyCta")}
        message={t("bulk.applyConfirmMessage", { n: num(needsReviewCount, lang) })}
      />
    </div>
  );
}
