import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { Plus, Trash2, Edit2, ChevronDown } from "lucide-react";
import { listExams, createExam, updateExam, deleteExam, uploadExamRubricFile } from "../api/exams";
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
  ConfirmDialog,
  friendlyError,
} from "../components/ui";
import { ACCEPTED_TYPES } from "../components/FileAttachment";
import { formatNumber, timeAgo } from "../utils/format";
import { currentLang } from "../hooks/useLang";
import { useToast } from "../hooks/useToast";
import { getAuthUser } from "../api/auth";
import { hasRole } from "../utils/roles";

interface ExamForm {
  name: string;
  year: number;
  description: string;
  rubricFile: File | null;
}

const emptyForm = (): ExamForm => ({
  name: "",
  year: new Date().getFullYear(),
  description: "",
  rubricFile: null,
});

export default function ExamsPage() {
  const { t } = useTranslation();
  const lang = currentLang();
  const qc = useQueryClient();
  const toast = useToast();
  const navigate = useNavigate();
  // Exam create/edit/delete are Teacher-only server-side — hide the controls.
  const canManage = hasRole(getAuthUser(), "Teacher");

  const examsQ = useQuery({ queryKey: ["exams"], queryFn: listExams });

  const [showCreate, setShowCreate] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<string | null>(null);
  const [form, setForm] = useState<ExamForm>(emptyForm());

  const invalidate = () => qc.invalidateQueries({ queryKey: ["exams"] });

  const createMut = useMutation({
    // Create, then (optionally) upload the exam-wide rubric file, then land on
    // the exam page — with ?extract=1 the extraction preview auto-opens.
    mutationFn: async () => {
      const exam = await createExam({
        name: form.name.trim(),
        year: form.year,
        description: form.description.trim() || null,
      });
      if (form.rubricFile) await uploadExamRubricFile(exam.id, form.rubricFile);
      return exam;
    },
    onSuccess: (exam) => {
      invalidate();
      closeDialog();
      toast.success(t("states.reviewSaved"));
      navigate(`/exams/${exam.id}${form.rubricFile ? "?extract=1" : ""}`);
    },
    onError: (e) => toast.error(friendlyError(e, t)),
  });

  const updateMut = useMutation({
    mutationFn: () =>
      updateExam(editingId!, {
        name: form.name.trim(),
        year: form.year,
        description: form.description.trim() || null,
      }),
    onSuccess: () => {
      invalidate();
      closeDialog();
      toast.success(t("states.reviewSaved"));
    },
    onError: (e) => toast.error(friendlyError(e, t)),
  });

  const deleteMut = useMutation({
    mutationFn: (id: string) => deleteExam(id),
    onSuccess: () => {
      invalidate();
      setDeleteTarget(null);
      toast.success(t("common.delete") + " ✓");
    },
    onError: (e) => toast.error(friendlyError(e, t)),
  });

  function openCreate() {
    setForm(emptyForm());
    setEditingId(null);
    setShowCreate(true);
  }

  function openEdit(id: string) {
    const e = examsQ.data?.find((x) => x.id === id);
    if (!e) return;
    setForm({
      name: e.name,
      year: e.year,
      description: e.description ?? "",
      rubricFile: null,
    });
    setEditingId(id);
    setShowCreate(true);
  }

  function closeDialog() {
    setShowCreate(false);
    setEditingId(null);
  }

  if (examsQ.isLoading) return <LoadingBlock />;
  if (examsQ.isError)
    return (
      <ErrorState message={friendlyError(examsQ.error, t)} onRetry={() => examsQ.refetch()} />
    );

  const dialogOpen = showCreate || !!editingId;

  return (
    <>
      <PageHeader
        title={t("exams.title")}
        subtitle={t("exams.subtitle")}
        action={
          canManage ? (
            <Button onClick={openCreate}>
              <Plus size={16} /> {t("exams.createExam")}
            </Button>
          ) : undefined
        }
      />

      {!examsQ.data?.length ? (
        <Card>
          <EmptyState title={t("exams.noExams")} hint={t("exams.noExamsHint")} />
        </Card>
      ) : (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {examsQ.data.map((e) => (
            <Card key={e.id}>
              <div className="mb-2 flex items-start justify-between gap-2">
                <Link to={`/exams/${e.id}`} className="font-semibold text-zinc-900 hover:text-primary-700">
                  {e.name}
                </Link>
                <Badge tone="zinc">{formatNumber(e.year, lang)}</Badge>
              </div>
              <p className="mb-3 line-clamp-2 min-h-[2rem] text-sm text-zinc-500">
                {e.description || "—"}
              </p>
              <p className="text-[11px] text-zinc-400">
                {timeAgo(e.createdAt, lang)} · {e.createdByName}
              </p>
              <div className="mt-4 flex items-center gap-1.5">
                <Link to={`/exams/${e.id}`} className="flex-1">
                  <Button variant="secondary" size="sm" className="w-full justify-center">
                    <ChevronDown size={14} /> {t("exams.openExam")}
                  </Button>
                </Link>
                {canManage ? (
                  <>
                    <Button variant="ghost" size="sm" onClick={() => openEdit(e.id)} aria-label={t("common.edit")}>
                      <Edit2 size={14} />
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      className="text-red-600 hover:bg-red-50"
                      onClick={() => setDeleteTarget(e.id)}
                      aria-label={t("common.delete")}
                    >
                      <Trash2 size={14} />
                    </Button>
                  </>
                ) : null}
              </div>
            </Card>
          ))}
        </div>
      )}

      {/* Create / edit dialog */}
      <Dialog open={dialogOpen} onClose={closeDialog} title={editingId ? t("exams.editExam") : t("exams.createExam")}>
        <form
          onSubmit={(ev) => {
            ev.preventDefault();
            if (!form.name.trim()) return;
            if (editingId) updateMut.mutate();
            else createMut.mutate();
          }}
        >
          <div className="grid gap-3">
            <Field label={t("exams.examName")} required htmlFor="exam-name">
              <Input
                id="exam-name"
                value={form.name}
                onChange={(e) => setForm({ ...form, name: e.target.value })}
                required
              />
            </Field>
            <Field label={t("exams.year")} required htmlFor="exam-year">
              <Input
                id="exam-year"
                type="number"
                min={1900}
                max={2200}
                value={form.year}
                onChange={(e) => setForm({ ...form, year: Number(e.target.value) })}
                required
              />
            </Field>
            <Field label={`${t("exams.description")} (${t("common.optional")})`} htmlFor="exam-desc">
              <Textarea
                id="exam-desc"
                rows={3}
                value={form.description}
                onChange={(e) => setForm({ ...form, description: e.target.value })}
              />
            </Field>
            {!editingId ? (
              <Field
                label={`${t("files.examRubricLabel")} (${t("common.optional")})`}
                hint={t("exams.rubricFileHint")}
                htmlFor="exam-rubric-file"
              >
                <Input
                  id="exam-rubric-file"
                  type="file"
                  accept={ACCEPTED_TYPES}
                  onChange={(e) => setForm({ ...form, rubricFile: e.target.files?.[0] ?? null })}
                />
              </Field>
            ) : null}
          </div>
          <div className="mt-5 flex justify-end gap-2">
            <Button type="button" variant="secondary" onClick={closeDialog}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" loading={createMut.isPending || updateMut.isPending} disabled={createMut.isPending || updateMut.isPending}>
              {t("common.save")}
            </Button>
          </div>
        </form>
      </Dialog>

      {/* Delete confirmation */}
      <ConfirmDialog
        open={!!deleteTarget}
        onClose={() => setDeleteTarget(null)}
        onConfirm={() => deleteMut.mutate(deleteTarget!)}
        title={t("common.confirmDeleteTitle", {
          name: examsQ.data?.find((x) => x.id === deleteTarget)?.name ?? "",
        })}
        message={t("exams.deleteWarning")}
        loading={deleteMut.isPending}
      />
    </>
  );
}
