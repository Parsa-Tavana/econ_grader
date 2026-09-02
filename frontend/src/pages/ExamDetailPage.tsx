import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import {
  Plus,
  Trash2,
  Edit2,
  ArrowLeft,
  Upload,
  Layers,
  Sparkles,
} from "lucide-react";
import type { ApplyExtractionQuestion, QuestionDto } from "../types/models";
import {
  getExam,
  uploadExamRubricFile,
  deleteExamRubricFile,
  extractExamQuestions,
  applyExtraction,
  examRubricFileUrl,
} from "../api/exams";
import {
  listQuestionsByExam,
  createQuestion,
  updateQuestion,
  deleteQuestion,
  getActiveRubric,
  uploadQuestionFile,
  deleteQuestionFile,
  questionFileUrl,
} from "../api/questions";
import { listAnswersByQuestion, uploadAnswer } from "../api/answers";
import { listStudents, createStudent } from "../api/students";
import { apiErrorMessage } from "../api/client";
import { FileAttachment } from "../components/FileAttachment";
import { ExtractionPreviewDialog } from "../components/ExtractionPreviewDialog";
import {
  PageHeader,
  Card,
  Badge,
  Input,
  Textarea,
  Field,
  Button,
  LoadingBlock,
  ErrorState,
  EmptyState,
  Dialog,
  friendlyError,
} from "../components/ui";
import { formatScore, formatNumber } from "../utils/format";
import { currentLang } from "../hooks/useLang";
import { useToast } from "../hooks/useToast";
import { getAuthUser } from "../api/auth";
import { hasRole } from "../utils/roles";

interface QuestionForm {
  number: number;
  text: string;
  maxScore: number;
}

const emptyQuestion = (): QuestionForm => ({
  number: 1,
  text: "",
  maxScore: 20,
});

export default function ExamDetailPage() {
  const { examId = "" } = useParams();
  const { t } = useTranslation();
  const qc = useQueryClient();
  const toast = useToast();
  // Question/rubric/file mutations are Teacher-only server-side — hide the controls.
  const canManage = hasRole(getAuthUser(), "Teacher");

  const examQ = useQuery({ queryKey: ["exam", examId], queryFn: () => getExam(examId) });
  const questionsQ = useQuery({
    queryKey: ["questions", examId],
    queryFn: () => listQuestionsByExam(examId),
  });

  // question dialog state
  const [qDialogOpen, setQDialogOpen] = useState(false);
  const [editingQId, setEditingQId] = useState<string | null>(null);
  const [qForm, setQForm] = useState<QuestionForm>(emptyQuestion());

  // extraction preview dialog state
  const [extractionOpen, setExtractionOpen] = useState(false);
  const [extractionError, setExtractionError] = useState<string | null>(null);
  const extractMut = useMutation({
    // The AI call runs server-side (POST /extraction/preview); nothing is
    // saved until the user confirms rows in the preview (POST /apply).
    mutationFn: () => extractExamQuestions(examId),
    onSuccess: () => setExtractionError(null),
    onError: (e) => setExtractionError(friendlyError(apiErrorMessage(e), t)),
  });
  const applyMut = useMutation({
    mutationFn: (questions: ApplyExtractionQuestion[]) => applyExtraction(examId, questions),
    onSuccess: (result) => {
      qc.invalidateQueries({ queryKey: ["questions", examId] });
      qc.invalidateQueries({ queryKey: ["exam", examId] });
      setExtractionOpen(false);
      toast.success(
        t("extraction.applied", {
          created: result.createdQuestions,
          updated: result.updatedQuestions,
        })
      );
    },
    onError: (e) => toast.error(friendlyError(apiErrorMessage(e), t)),
  });

  function runExtraction() {
    // Drop any previous preview so the dialog shows the spinner, not stale rows.
    extractMut.reset();
    setExtractionOpen(true);
    setExtractionError(null);
    extractMut.mutate();
  }

  // Creating an exam with a rubric file lands here with ?extract=1 — auto-open
  // the preview once. The ref guard survives StrictMode double-mount.
  const [searchParams, setSearchParams] = useSearchParams();
  const autoExtractFired = useRef(false);
  useEffect(() => {
    if (
      !autoExtractFired.current &&
      canManage &&
      searchParams.get("extract") === "1" &&
      examQ.data?.rubricFileName
    ) {
      autoExtractFired.current = true;
      setSearchParams({}, { replace: true });
      runExtraction();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [canManage, searchParams, examQ.data?.rubricFileName]);

  function openCreateQuestion() {
    setQForm({ ...emptyQuestion(), number: (questionsQ.data?.length ?? 0) + 1 });
    setEditingQId(null);
    setQDialogOpen(true);
  }

  function openEditQuestion(q: QuestionDto) {
    setQForm({ number: q.number, text: q.text, maxScore: q.maxScore });
    setEditingQId(q.id);
    setQDialogOpen(true);
  }

  const saveQuestionMut = useMutation({
    mutationFn: async () => {
      if (editingQId) {
        await updateQuestion(editingQId, {
          text: qForm.text.trim(),
          maxScore: qForm.maxScore,
        });
      } else {
        await createQuestion({
          examId,
          number: qForm.number,
          text: qForm.text.trim(),
          maxScore: qForm.maxScore,
        });
      }
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["questions", examId] });
      setQDialogOpen(false);
      toast.success(t("states.reviewSaved"));
    },
    onError: (e) => toast.error(friendlyError(e, t)),
  });

  const deleteQuestionMut = useMutation({
    mutationFn: deleteQuestion,
    onSuccess: () => qc.invalidateQueries({ queryKey: ["questions", examId] }),
    onError: (e) => toast.error(friendlyError(e, t)),
  });

  async function handleRubricFileUpload(file: File) {
    await uploadExamRubricFile(examId, file);
    await qc.invalidateQueries({ queryKey: ["exam", examId] });
  }
  async function handleRubricFileDelete() {
    await deleteExamRubricFile(examId);
    await qc.invalidateQueries({ queryKey: ["exam", examId] });
  }

  if (examQ.isLoading || questionsQ.isLoading) return <LoadingBlock />;
  if (examQ.isError)
    return <ErrorState message={friendlyError(examQ.error, t)} onRetry={() => examQ.refetch()} />;

  const exam = examQ.data!;

  return (
    <>
      <PageHeader
        title={exam.name}
        subtitle={`${t("exams.questionCountLabel", { count: questionsQ.data?.length ?? 0 })}${exam.description ? ` · ${exam.description}` : ""}`}
        action={
          <div className="flex gap-2">
            <Link to="/exams">
              <Button variant="ghost">
                <ArrowLeft size={16} /> {t("common.back")}
              </Button>
            </Link>
            {canManage ? (
              <Button onClick={openCreateQuestion}>
                <Plus size={16} /> {t("questions.addQuestion")}
              </Button>
            ) : null}
          </div>
        }
      />

      {/* ── Exam-wide rubric (grading key) ────────────────────────────────
          One file per exam: the AI extracts every question + its criteria
          from it. Grading never re-reads this file — it uses the saved,
          versioned rubric rows (editable in the grading workspace). */}
      <Card className="mb-4">
        <div className="flex flex-wrap items-start gap-4">
          <div className="min-w-[240px] flex-1">
            <FileAttachment
              label={t("files.examRubricLabel")}
              fileName={exam.rubricFileName ?? null}
              contentType={exam.rubricFileContentType ?? null}
              fileUrl={exam.rubricFileName ? examRubricFileUrl(examId) : null}
              onUpload={handleRubricFileUpload}
              onDelete={handleRubricFileDelete}
              canEdit={canManage}
            />
          </div>
          {canManage ? (
            <div className="flex flex-col items-stretch justify-center gap-2">
              <Button onClick={runExtraction} disabled={!exam.rubricFileName} loading={extractMut.isPending && !extractionOpen}>
                <Sparkles size={15} /> {t("exams.extractQuestions")}
              </Button>
              <p className="max-w-[280px] text-[11px] leading-relaxed text-zinc-400">
                {t("exams.rubricFileHintLong")}
              </p>
            </div>
          ) : null}
        </div>
      </Card>

      {!questionsQ.data?.length ? (
        <Card>
          <EmptyState
            title={t("questions.title")}
            hint={canManage && exam.rubricFileName ? t("exams.extractHint") : t("questions.noQuestionsHint")}
            action={
              canManage ? (
                exam.rubricFileName ? (
                  <Button onClick={runExtraction} loading={extractMut.isPending && !extractionOpen}>
                    <Sparkles size={15} /> {t("exams.extractQuestions")}
                  </Button>
                ) : (
                  <Button onClick={openCreateQuestion}>
                    <Plus size={15} /> {t("questions.addQuestion")}
                  </Button>
                )
              ) : undefined
            }
          />
        </Card>
      ) : (
        <div className="grid gap-4 lg:grid-cols-2">
          {questionsQ.data.map((q) => (
            <QuestionCard
              key={q.id}
              question={q}
              canManage={canManage}
              onEdit={() => openEditQuestion(q)}
              onDelete={() => deleteQuestionMut.mutate(q.id)}
            />
          ))}
        </div>
      )}

      {/* Question create/edit dialog */}
      <Dialog
        open={qDialogOpen}
        onClose={() => setQDialogOpen(false)}
        title={editingQId ? t("questions.editQuestion") : t("questions.addQuestion")}
      >
        <form
          onSubmit={(e) => {
            e.preventDefault();
            if (!qForm.text.trim()) return;
            saveQuestionMut.mutate();
          }}
        >
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label={t("questions.number")} required htmlFor="q-num">
              <Input
                id="q-num"
                type="number"
                min={1}
                value={qForm.number}
                onChange={(e) => setQForm({ ...qForm, number: Number(e.target.value) })}
                required
              />
            </Field>
            <Field label={t("questions.maxScore")} required htmlFor="q-max">
              <Input
                id="q-max"
                type="number"
                min={0.5}
                step={0.5}
                value={qForm.maxScore}
                onChange={(e) => setQForm({ ...qForm, maxScore: Number(e.target.value) })}
                required
              />
            </Field>
          </div>
          <div className="mt-3">
            <Field label={t("questions.text")} required htmlFor="q-text">
              <Textarea
                id="q-text"
                rows={3}
                value={qForm.text}
                onChange={(e) => setQForm({ ...qForm, text: e.target.value })}
                required
              />
            </Field>
          </div>
          <div className="mt-5 flex justify-end gap-2">
            <Button type="button" variant="secondary" onClick={() => setQDialogOpen(false)}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" loading={saveQuestionMut.isPending}>
              {t("common.save")}
            </Button>
          </div>
        </form>
      </Dialog>

      {/* AI extraction preview — rows are editable; Apply posts to /apply */}
      <ExtractionPreviewDialog
        open={extractionOpen}
        onClose={() => setExtractionOpen(false)}
        preview={extractMut.data ?? null}
        running={extractMut.isPending}
        error={extractionError}
        existingNumbers={(questionsQ.data ?? []).map((q) => q.number)}
        applying={applyMut.isPending}
        onApply={(questions) => applyMut.mutate(questions)}
      />
    </>
  );
}

function QuestionCard({
  question,
  canManage,
  onEdit,
  onDelete,
}: {
  question: QuestionDto;
  /** Teacher-only controls (edit/delete/file uploads) render when true. */
  canManage: boolean;
  onEdit: () => void;
  onDelete: () => void;
}) {
  const { t } = useTranslation();
  const lang = currentLang();
  const qc = useQueryClient();
  const rubricQ = useQuery({
    queryKey: ["rubric", question.id],
    queryFn: () => getActiveRubric(question.id),
    retry: false,
    staleTime: Infinity,
  });
  const answersQ = useQuery({
    queryKey: ["answers", "question", question.id],
    queryFn: () => listAnswersByQuestion(question.id),
  });
  const studentsQ = useQuery({ queryKey: ["students"], queryFn: listStudents });
  const [uploading, setUploading] = useState(false);
  const toast = useToast();

  async function handleUpload(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    const externalId = window.prompt(t("students.externalIdPrompt"));
    if (!externalId?.trim()) {
      e.target.value = "";
      return;
    }
    setUploading(true);
    try {
      let student = studentsQ.data?.find((s) => s.externalId === externalId.trim());
      if (!student) {
        student = await createStudent({ externalId: externalId.trim() });
        await qc.invalidateQueries({ queryKey: ["students"] });
      }
      await uploadAnswer(student.id, question.id, file);
      await qc.invalidateQueries({ queryKey: ["answers", "question", question.id] });
      toast.success(t("answers.uploadSuccess"));
    } catch (err) {
      toast.error(friendlyError(err, t));
    } finally {
      setUploading(false);
      e.target.value = "";
    }
  }

  async function handleQuestionFile(file: File) {
    await uploadQuestionFile(question.id, file);
    await qc.invalidateQueries({ queryKey: ["questions", question.examId] });
  }
  async function handleDeleteQuestionFile() {
    await deleteQuestionFile(question.id);
    await qc.invalidateQueries({ queryKey: ["questions", question.examId] });
  }

  return (
    <Card>
      <div className="mb-2 flex items-start justify-between gap-2">
        <h3 className="font-semibold text-zinc-900">
          {t("questions.questionN", { number: question.number })}
        </h3>
        <div className="flex items-center gap-1.5">
          <Badge tone={rubricQ.data ? "green" : "amber"}>
            <Layers size={11} />
            {t("questions.rubricStatus")}
          </Badge>
          <Badge tone="zinc">{formatScore(question.maxScore, lang)}</Badge>
        </div>
      </div>

      <p className="mb-3 line-clamp-3 text-sm text-zinc-600">{question.text}</p>

      {/* Question paper attachment. The rubric lives on the exam (see the card
          above); per-question rubric files were removed with the new flow. */}
      <div className="mb-3">
        <FileAttachment
          label={t("files.questionLabel")}
          fileName={question.fileName ?? null}
          contentType={question.contentType ?? null}
          fileUrl={question.fileName ? questionFileUrl(question.id) : null}
          onUpload={handleQuestionFile}
          onDelete={handleDeleteQuestionFile}
          canEdit={canManage}
        />
      </div>

      {rubricQ.data ? (
        <ul className="mb-3 space-y-1 rounded-lg bg-zinc-50 p-2.5 text-xs text-zinc-600">
          {rubricQ.data.criteria.map((c) => (
            <li key={c.criterionId} className="flex justify-between gap-2">
              <span className="line-clamp-1">{c.description}</span>
              <span className="shrink-0 font-medium tabular-nums">{formatScore(c.maxScore, lang)}</span>
            </li>
          ))}
        </ul>
      ) : null}

      <div className="flex flex-wrap items-center gap-2 border-t border-zinc-100 pt-3">
        {canManage ? (
          <label
            className={
              "inline-flex cursor-pointer items-center gap-1.5 rounded-xl border px-3 py-1.5 text-xs font-medium transition " +
              (uploading
                ? "cursor-wait border-zinc-200 bg-zinc-50 text-zinc-400"
                : "border-zinc-200 bg-zinc-100 text-zinc-900 hover:bg-zinc-200")
            }
          >
            <Upload size={13} />
            {uploading ? t("answers.uploading") : t("answers.uploadAnswer")}
            <input type="file" accept=".pdf,.png,.jpg,.jpeg,.docx,.xlsx,.xls" className="hidden" onChange={handleUpload} disabled={uploading} />
          </label>
        ) : null}
        <Link to={`/grading/queue?questionId=${question.id}`} className="text-xs font-medium text-primary-600 hover:underline">
          {t("queue.openQueue")} ({formatNumber(answersQ.data?.length ?? 0, lang)})
        </Link>
        <span className="flex-1" />
        {canManage ? (
          <>
            <Button size="sm" variant="ghost" onClick={onEdit} aria-label={t("common.edit")}>
              <Edit2 size={13} />
            </Button>
            <Button size="sm" variant="ghost" className="text-red-600 hover:bg-red-50" onClick={onDelete} aria-label={t("common.delete")}>
              <Trash2 size={13} />
            </Button>
          </>
        ) : null}
      </div>
    </Card>
  );
}
