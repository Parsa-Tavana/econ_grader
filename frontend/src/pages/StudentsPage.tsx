import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { Plus, GraduationCap, Search } from "lucide-react";
import { listStudents, createStudent } from "../api/students";
import {
  PageHeader,
  Card,
  Badge,
  Input,
  Field,
  Button,
  LoadingBlock,
  ErrorState,
  EmptyState,
  Dialog,
  friendlyError,
} from "../components/ui";
import { timeAgo } from "../utils/format";
import { currentLang } from "../hooks/useLang";
import { useDebounce } from "../hooks/useDebounce";

export default function StudentsPage() {
  const { t } = useTranslation();
  const lang = currentLang();
  const qc = useQueryClient();
  const studentsQ = useQuery({ queryKey: ["students"], queryFn: listStudents });

  const [search, setSearch] = useState("");
  const debounced = useDebounce(search);
  const [showCreate, setShowCreate] = useState(false);
  const [externalId, setExternalId] = useState("");
  const [displayName, setDisplayName] = useState("");

  const filtered = useMemo(() => {
    const q = debounced.trim().toLowerCase();
    if (!q) return studentsQ.data ?? [];
    return (
      studentsQ.data?.filter(
        (s) =>
          s.externalId.toLowerCase().includes(q) ||
          (s.displayName ?? "").toLowerCase().includes(q)
      ) ?? []
    );
  }, [studentsQ.data, debounced]);

  const createMut = useMutation({
    mutationFn: () =>
      createStudent({ externalId: externalId.trim(), displayName: displayName.trim() || null }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["students"] });
      setShowCreate(false);
      setExternalId("");
      setDisplayName("");
    },
    onError: (e) => alert(friendlyError(e, t)),
  });

  if (studentsQ.isLoading) return <LoadingBlock />;
  if (studentsQ.isError)
    return (
      <ErrorState message={friendlyError(studentsQ.error, t)} onRetry={() => studentsQ.refetch()} />
    );

  return (
    <>
      <PageHeader
        title={t("students.title")}
        subtitle={t("students.subtitle")}
        action={
          <Button onClick={() => setShowCreate(true)}>
            <Plus size={16} /> {t("students.addStudent")}
          </Button>
        }
      />

      <div className="mb-4 flex items-center gap-2">
        <div className="relative max-w-xs flex-1">
          <Search
            size={15}
            className="pointer-events-none absolute top-1/2 -translate-y-1/2 text-zinc-400 rtl:right-3 ltr:left-3"
          />
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder={t("common.search")}
            className="rtl:pr-9 ltr:pl-9"
          />
        </div>
        <Badge tone="zinc">{t("students.countLabel", { count: filtered.length })}</Badge>
      </div>

      {!filtered.length ? (
        <Card>
          <EmptyState title={t("students.noStudents")} hint={t("students.noStudentsHint")} />
        </Card>
      ) : (
        <Card className="overflow-x-auto p-0">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-zinc-200 text-xs uppercase tracking-wide text-zinc-400">
                <th className="px-4 py-3 text-start font-medium">{t("students.externalId")}</th>
                <th className="px-4 py-3 text-start font-medium">{t("students.displayName")}</th>
                <th className="px-4 py-3 text-start font-medium">{t("common.createdAt")}</th>
                <th className="px-4 py-3" />
              </tr>
            </thead>
            <tbody>
              {filtered.map((s) => (
                <tr key={s.id} className="border-b border-zinc-50 transition hover:bg-zinc-50/60">
                  <td className="px-4 py-3 font-medium text-zinc-800">
                    <GraduationCap size={14} className="inline-block align-[-2px] text-zinc-400 ltr:mr-1.5 rtl:ml-1.5" />
                    {s.externalId}
                  </td>
                  <td className="px-4 py-3 text-zinc-600">{s.displayName || "—"}</td>
                  <td className="px-4 py-3 text-xs text-zinc-400">{timeAgo(s.createdAt, lang)}</td>
                  <td className="px-4 py-3 text-end">
                    <Link to={`/students/${s.id}`}>
                      <Button size="sm" variant="ghost">
                        {t("common.viewDetails")}
                      </Button>
                    </Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}

      <Dialog open={showCreate} onClose={() => setShowCreate(false)} title={t("students.addStudent")}>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            if (!externalId.trim()) return;
            createMut.mutate();
          }}
        >
          <div className="grid gap-3">
            <Field label={t("students.externalId")} required htmlFor="st-ext">
              <Input
                id="st-ext"
                value={externalId}
                onChange={(e) => setExternalId(e.target.value)}
                required
                placeholder="S001"
              />
            </Field>
            <Field label={`${t("students.displayName")} (${t("common.optional")})`} htmlFor="st-name">
              <Input id="st-name" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
            </Field>
          </div>
          <p className="mt-3 rounded-lg bg-zinc-50 p-2.5 text-[11px] leading-relaxed text-zinc-500">
            {t("answers.uniquePerStudentQuestion")}
          </p>
          <div className="mt-5 flex justify-end gap-2">
            <Button type="button" variant="secondary" onClick={() => setShowCreate(false)}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" loading={createMut.isPending}>
              {t("common.save")}
            </Button>
          </div>
        </form>
      </Dialog>
    </>
  );
}
