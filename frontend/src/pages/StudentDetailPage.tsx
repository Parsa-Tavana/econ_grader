import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { ArrowRight, Upload } from "lucide-react";
import type { QuestionDto } from "../types/models";
import { getStudent } from "../api/students";
import { listExams } from "../api/exams";
import { listQuestionsByExam } from "../api/questions";
import {
  listAnswersByQuestion,
  uploadAnswer,
} from "../api/answers";
import {
  PageHeader,
  Card,
  CardHeader,
  Badge,
  Button,
  LoadingBlock,
  ErrorState,
  EmptyState,
  friendlyError,
} from "../components/ui";
import { AnswerStatusBadge } from "../components/common";

export default function StudentDetailPage() {
  const { studentId = "" } = useParams();
  const { t } = useTranslation();
  const navigate = useNavigate();

  const studentQ = useQuery({
    queryKey: ["student", studentId],
    queryFn: () => getStudent(studentId),
  });

  if (studentQ.isLoading) return <LoadingBlock />;
  if (studentQ.isError)
    return (
      <ErrorState message={friendlyError(studentQ.error, t)} onRetry={() => studentQ.refetch()} />
    );

  const s = studentQ.data!;

  return (
    <>
      <PageHeader
        title={s.displayName || s.externalId}
        subtitle={`${t("students.externalId")}: ${s.externalId}`}
      />

      <AnswersByQuestionPanel studentId={s.id} />

      <div className="mt-6">
        <span className="text-xs font-medium text-zinc-500 hover:text-primary-600 cursor-pointer" onClick={() => navigate("/students")}>
          ← {t("students.title")}
        </span>
      </div>
    </>
  );
}

/** Loads all exams/questions and shows this student's answer per question. */
function AnswersByQuestionPanel({ studentId }: { studentId: string }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [uploadingKey, setUploadingKey] = useState<string | null>(null);

  const examsQ = useQuery({ queryKey: ["exams"], queryFn: listExams });

  const questionLists = useQuery({
    queryKey: ["all-questions", examsQ.data?.map((e) => e.id).join(",")],
    queryFn: async (): Promise<QuestionDto[]> => {
      const lists = await Promise.all(
        (examsQ.data ?? []).map((e) => listQuestionsByExam(e.id))
      );
      return lists.flat();
    },
    enabled: !!examsQ.data?.length,
  });

  if (examsQ.isLoading || questionLists.isLoading) return <LoadingBlock />;
  const questions = questionLists.data ?? [];

  return (
    <Card>
      <CardHeader
        title={t("students.answersTitle")}
        subtitle={t("answers.uniquePerStudentQuestion")}
      />
      {!questions.length ? (
        <EmptyState title={t("questions.title")} hint={t("queue.uploadFromExamHint")} />
      ) : (
        <ul className="divide-y divide-zinc-100">
          {questions.map((q) => (
            <li key={q.id}>
              <AnswerRow
                question={q}
                studentId={studentId}
                uploading={uploadingKey === q.id}
                onUploading={(v) => setUploadingKey(v ? q.id : null)}
                onOpen={() => navigate(`/grading/queue?questionId=${q.id}`)}
              />
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}

function AnswerRow({
  question,
  studentId,
  uploading,
  onUploading,
  onOpen,
}: {
  question: QuestionDto;
  studentId: string;
  uploading: boolean;
  onUploading: (v: boolean) => void;
  onOpen: () => void;
}) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const answersQ = useQuery({
    queryKey: ["answers", "question", question.id],
    queryFn: () => listAnswersByQuestion(question.id),
  });
  const mine = (answersQ.data ?? []).find((a) => a.studentId === studentId);

  async function handleFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;
    onUploading(true);
    try {
      await uploadAnswer(studentId, question.id, file);
      await qc.invalidateQueries({ queryKey: ["answers", "question", question.id] });
    } catch (err) {
      alert(friendlyError(err, t));
    } finally {
      onUploading(false);
    }
  }

  return (
    <div className="flex flex-wrap items-center gap-2 py-2.5">
      <span className="w-28 shrink-0 text-sm font-medium text-zinc-800">
        {t("questions.questionN", { number: question.number })}
      </span>
      <span className="min-w-0 flex-1 truncate text-sm text-zinc-500">{question.text}</span>
      {mine ? (
        <>
          <AnswerStatusBadge answer={mine} />
          <Button size="sm" variant="ghost" onClick={onOpen}>
            {t("workspace.openWorkspace")}
            <ArrowRight size={13} />
          </Button>
        </>
      ) : (
        <Badge tone="zinc">{t("status.noAi")}</Badge>
      )}
      <label className="inline-flex cursor-pointer items-center gap-1.5 rounded-xl border border-zinc-200 bg-zinc-100 px-3 py-1.5 text-xs font-medium text-zinc-900 transition hover:bg-zinc-200">
        <Upload size={13} />
        {uploading
          ? t("answers.uploading")
          : mine
            ? t("answers.replaceImage")
            : t("answers.uploadAnswer")}
        <input
          type="file"
          accept="image/png,image/jpeg"
          className="hidden"
          onChange={handleFile}
          disabled={uploading}
        />
      </label>
    </div>
  );
}