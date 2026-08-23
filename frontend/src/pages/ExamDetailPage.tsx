import { clsx } from "clsx";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Link, useParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import {
  Plus,
  Trash2,
  Edit2,
  ArrowLeft,
  Upload,
  Layers,
} from "lucide-react";
import type { QuestionDto } from "../types/models";
import { getExam } from "../api/exams";
import {
  listQuestionsByExam,
  createQuestion,
  updateQuestion,
  deleteQuestion,
  getActiveRubric,
  createRubric,
} from "../api/questions";
import { listAnswersByQuestion } from "../api/answers";
import { uploadAnswer } from "../api/answers";
import { listStudents, createStudent } from "../api/students";
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

interface RubricCriterionInput {
  description: string;
  maxScore: number;
}

interface QuestionForm {
  number: number;
  text: string;
  maxScore: number;
  rubricText: string;
}

const emptyQuestion = (): QuestionForm => ({
  number: 1,
  text: "",
  maxScore: 20,
  rubricText: "",
});

export default function ExamDetailPage() {
  const { examId = "" } = useParams();
  const { t } = useTranslation();
  const qc = useQueryClient();
  const toast = useToast();

  const examQ = useQuery({ queryKey: ["exam", examId], queryFn: () => getExam(examId) });
  const questionsQ = useQuery({
    queryKey: ["questions", examId],
    queryFn: () => listQuestionsByExam(examId),
  });

  // question dialog state
  const [qDialogOpen, setQDialogOpen] = useState(false);
  const [editingQId, setEditingQId] = useState<string | null>(null);
  const [qForm, setQForm] = useState<QuestionForm>(emptyQuestion());

  // rubric dialog state
  const [rubricQ, setRubricQ] = useState<QuestionDto | null>(null);
  const [criteria, setCriteria] = useState<RubricCriterionInput[]>([]);

  function openCreateQuestion() {
    setQForm({ ...emptyQuestion(), number: (questionsQ.data?.length ?? 0) + 1 });
    setEditingQId(null);
    setQDialogOpen(true);
  }

  function openEditQuestion(q: QuestionDto) {
    setQForm({ number: q.number, text: q.text, maxScore: q.maxScore, rubricText: q.rubricText ?? "" });
    setEditingQId(q.id);
    setQDialogOpen(true);
  }

  async function openRubric(q: QuestionDto) {
    setRubricQ(q);
    try {
      const r = await getActiveRubric(q.id);
      setCriteria(
        r?.criteria.map((c) => ({ description: c.description, maxScore: c.maxScore })) ?? []
      );
    } catch {
      setCriteria([]);
    }
  }

  const saveQuestionMut = useMutation({
    mutationFn: async () => {
      if (editingQId) {
        await updateQuestion(editingQId, {
          text: qForm.text.trim(),
          maxScore: qForm.maxScore,
          rubricText: qForm.rubricText || null,
        });
      } else {
        await createQuestion({
          examId,
          number: qForm.number,
          text: qForm.text.trim(),
          maxScore: qForm.maxScore,
          rubricText: qForm.rubricText || null,
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

  const saveRubricMut = useMutation({
    mutationFn: () =>
      createRubric({
        questionId: rubricQ!.id,
        criteria: criteria
          .filter((c) => c.description.trim())
          .map((c, i) => ({
            criterionId: crypto.randomUUID(),
            description: c.description.trim(),
            maxScore: c.maxScore,
            order: i,
          })),
      }),
    onSuccess: (_data, _vars) => {
      qc.invalidateQueries({ queryKey: ["rubric"] });
      setRubricQ(null);
      toast.success(t("rubric.versionCreated"));
    },
    onError: (e) => toast.error(friendlyError(e, t)),
  });

  if (examQ.isLoading || questionsQ.isLoading) return <LoadingBlock />;
  if (examQ.isError)
    return <ErrorState message={friendlyError(examQ.error, t)} onRetry={() => examQ.refetch()} />;

  return (
    <>
      <PageHeader
        title={examQ.data?.name ?? ""}
        subtitle={`${t("exams.questionCountLabel", { count: questionsQ.data?.length ?? 0 })}${examQ.data?.description ? ` · ${examQ.data.description}` : ""}`}
        action={
          <div className="flex gap-2">
            <Link to="/exams">
              <Button variant="ghost">
                <ArrowLeft size={16} /> {t("common.back")}
              </Button>
            </Link>
            <Button onClick={openCreateQuestion}>
              <Plus size={16} /> {t("questions.addQuestion")}
            </Button>
          </div>
        }
      />

      {!questionsQ.data?.length ? (
        <Card>
          <EmptyState
            title={t("questions.title")}
            hint={t("exams.noExamsHint")}
            action={
              <Button onClick={openCreateQuestion}>
                <Plus size={15} /> {t("questions.addQuestion")}
              </Button>
            }
          />
        </Card>
      ) : (
        <div className="grid gap-4 lg:grid-cols-2">
          {questionsQ.data.map((q) => (
            <QuestionCard
              key={q.id}
              question={q}
              onEdit={() => openEditQuestion(q)}
              onDelete={() => deleteQuestionMut.mutate(q.id)}
              onOpenRubric={() => openRubric(q)}
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

      {/* Rubric editor */}
      <RubricEditor
        question={rubricQ}
        criteria={criteria}
        setCriteria={setCriteria}
        onClose={() => setRubricQ(null)}
        onSave={() => saveRubricMut.mutate()}
        saving={saveRubricMut.isPending}
      />
    </>
  );
}

function QuestionCard({
  question,
  onEdit,
  onDelete,
  onOpenRubric,
}: {
  question: QuestionDto;
  onEdit: () => void;
  onDelete: () => void;
  onOpenRubric: () => void;
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
        <Button size="sm" variant="secondary" onClick={onOpenRubric}>
          {t("rubric.editRubric")}
        </Button>
        <label
          className={clsx(
            "inline-flex cursor-pointer items-center gap-1.5 rounded-xl border px-3 py-1.5 text-xs font-medium transition",
            uploading
              ? "cursor-wait border-zinc-200 bg-zinc-50 text-zinc-400"
              : "border-zinc-200 bg-zinc-100 text-zinc-900 hover:bg-zinc-200"
          )}
        >
          <Upload size={13} />
          {uploading ? t("answers.uploading") : t("answers.uploadAnswer")}
          <input type="file" accept="image/png,image/jpeg" className="hidden" onChange={handleUpload} disabled={uploading} />
        </label>
        <Link to={`/grading/queue?questionId=${question.id}`} className="text-xs font-medium text-primary-600 hover:underline">
          {t("queue.openQueue")} ({formatNumber(answersQ.data?.length ?? 0, lang)})
        </Link>
        <span className="flex-1" />
        <Button size="sm" variant="ghost" onClick={onEdit} aria-label={t("common.edit")}>
          <Edit2 size={13} />
        </Button>
        <Button size="sm" variant="ghost" className="text-red-600 hover:bg-red-50" onClick={onDelete}>
          <Trash2 size={13} />
        </Button>
      </div>
    </Card>
  );
}

function RubricEditor({
  question,
  criteria,
  setCriteria,
  onClose,
  onSave,
  saving,
}: {
  question: QuestionDto | null;
  criteria: RubricCriterionInput[];
  setCriteria: (c: RubricCriterionInput[]) => void;
  onClose: () => void;
  onSave: () => void;
  saving: boolean;
}) {
  const { t } = useTranslation();
  const lang = currentLang();
  if (!question) return null;
  const total = criteria.reduce((s, c) => s + (Number(c.maxScore) || 0), 0);

  return (
    <Dialog
      open
      onClose={onClose}
      title={`${t("rubric.title")} — ${t("questions.questionN", { number: question.number })}`}
      description={t("rubric.hint")}
      wide
    >
      <div className="space-y-2.5">
        {criteria.map((c, i) => (
          <div key={i} className="flex items-start gap-2">
            <div className="flex-1">
              <Textarea
                rows={2}
                value={c.description}
                placeholder={t("rubric.criterionDescription")}
                onChange={(e) => {
                  const next = [...criteria];
                  next[i] = { ...next[i], description: e.target.value };
                  setCriteria(next);
                }}
              />
            </div>
            <div className="w-20">
              <Input
                type="number"
                min={0}
                step={0.5}
                value={c.maxScore}
                aria-label={t("questions.maxScore")}
                onChange={(e) => {
                  const next = [...criteria];
                  next[i] = { ...next[i], maxScore: Number(e.target.value) };
                  setCriteria(next);
                }}
              />
            </div>
            <button
              type="button"
              onClick={() => setCriteria(criteria.filter((_, j) => j !== i))}
              className="mt-2 rounded-lg p-1.5 text-zinc-400 transition hover:bg-red-50 hover:text-red-600"
              aria-label={t("common.delete")}
            >
              <Trash2 size={15} />
            </button>
          </div>
        ))}
        <Button
          variant="secondary"
          size="sm"
          onClick={() => setCriteria([...criteria, { description: "", maxScore: 5 }])}
        >
          <Plus size={14} /> {t("rubric.addCriterion")}
        </Button>
      </div>
      <p className="mt-3 text-xs text-zinc-500">
        {t("rubric.totalMaxScore")}:{" "}
        <strong className="tabular-nums">
          {formatScore(total, lang)} / {formatScore(question.maxScore, lang)}
        </strong>
      </p>
      <div className="mt-5 flex justify-end gap-2">
        <Button variant="secondary" onClick={onClose}>
          {t("common.cancel")}
        </Button>
        <Button onClick={onSave} loading={saving}>
          {t("common.save")}
        </Button>
      </div>
    </Dialog>
  );
}
