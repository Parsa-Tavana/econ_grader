import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Plus, ShieldCheck, ShieldOff, Pencil } from "lucide-react";
import { clsx } from "clsx";
import { listUsers, createUser, updateUser, type ManagedUser } from "../api/auth";
import {
  PageHeader,
  Card,
  Badge,
  Input,
  Field,
  Select,
  Button,
  LoadingBlock,
  EmptyState,
  ErrorState,
  Dialog,
  ConfirmDialog,
  friendlyError,
} from "../components/ui";
import { formatDateTime } from "../utils/format";
import { currentLang } from "../hooks/useLang";
import { useToast } from "../hooks/useToast";

const ROLES = ["Teacher", "Admin", "Corrector", "Student"] as const;

interface UserForm {
  email: string;
  password: string;
  displayName: string;
  role: (typeof ROLES)[number];
}

const emptyForm = (): UserForm => ({
  email: "",
  password: "",
  displayName: "",
  role: "Teacher",
});

/**
 * Maps auth-management error codes to i18n keys so admins see
 * "email already registered" instead of a raw problem-details title.
 */
function userActionError(err: unknown, t: (k: string) => string): string {
  const code = (err as { response?: { data?: { code?: string } } })?.response?.data
    ?.code;
  switch (code) {
    case "EMAIL_TAKEN":
      return t("users.emailTaken");
    case "WEAK_PASSWORD":
      return t("users.weakPassword");
    case "LAST_ADMIN":
      return t("users.lastAdmin");
    default:
      return friendlyError(err, t);
  }
}

export default function UsersPage() {
  const { t } = useTranslation();
  const lang = currentLang();
  const qc = useQueryClient();
  const toast = useToast();

  const usersQ = useQuery({ queryKey: ["auth-users"], queryFn: listUsers });

  const [showCreate, setShowCreate] = useState(false);
  const [form, setForm] = useState<UserForm>(emptyForm());
  // Deactivate/activate target (confirm dialog); demotion edits go through editRole.
  const [toggleTarget, setToggleTarget] = useState<ManagedUser | null>(null);
  const [editTarget, setEditTarget] = useState<ManagedUser | null>(null);
  const [editDisplayName, setEditDisplayName] = useState("");
  const [editRole, setEditRole] = useState<string>("Teacher");

  const invalidate = () => qc.invalidateQueries({ queryKey: ["auth-users"] });

  const createMut = useMutation({
    mutationFn: () =>
      createUser({
        email: form.email.trim(),
        password: form.password,
        displayName: form.displayName.trim(),
        role: form.role,
      }),
    onSuccess: (_u, _v) => {
      invalidate();
      setShowCreate(false);
      setForm(emptyForm());
      toast.success(t("users.created"));
    },
    onError: (e) => toast.error(userActionError(e, t)),
  });

  /** Activate / deactivate — backend guards the last active admin (409 LAST_ADMIN). */
  const toggleMut = useMutation({
    mutationFn: (u: ManagedUser) => updateUser(u.id, { isActive: !u.isActive }),
    onSuccess: () => {
      invalidate();
      setToggleTarget(null);
      toast.success(t("states.reviewSaved"));
    },
    onError: (e) => {
      setToggleTarget(null);
      toast.error(userActionError(e, t));
    },
  });

  const editMut = useMutation({
    mutationFn: () =>
      updateUser(editTarget!.id, {
        displayName: editDisplayName.trim(),
        role: editRole,
      }),
    onSuccess: () => {
      invalidate();
      setEditTarget(null);
      toast.success(t("states.reviewSaved"));
    },
    onError: (e) => {
      setEditTarget(null);
      toast.error(userActionError(e, t));
    },
  });

  if (usersQ.isLoading) return <LoadingBlock />;
  if (usersQ.isError)
    return (
      <ErrorState message={friendlyError(usersQ.error, t)} onRetry={() => usersQ.refetch()} />
    );

  const users = usersQ.data ?? [];

  return (
    <>
      <PageHeader
        title={t("users.title")}
        subtitle={t("users.subtitle")}
        action={
          <Button onClick={() => setShowCreate(true)}>
            <Plus size={16} /> {t("users.createUser")}
          </Button>
        }
      />

      {!users.length ? (
        <Card>
          <EmptyState title={t("users.noUsers")} hint={t("users.noUsersHint")} />
        </Card>
      ) : (
        <Card className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-zinc-100 text-start text-xs text-zinc-400">
                <th className="px-3 py-2 text-start font-medium">{t("users.name")}</th>
                <th className="px-3 py-2 text-start font-medium">{t("auth.email")}</th>
                <th className="px-3 py-2 text-start font-medium">{t("user.role")}</th>
                <th className="px-3 py-2 text-start font-medium">{t("common.status")}</th>
                <th className="px-3 py-2 text-start font-medium">{t("users.createdAt")}</th>
                <th className="px-3 py-2 text-end font-medium">{t("common.actions")}</th>
              </tr>
            </thead>
            <tbody>
              {users.map((u) => {
                const roleKey = `user.role${u.role}` as const;
                return (
                  <tr key={u.id} className="border-b border-zinc-50 last:border-0">
                    <td className="px-3 py-2.5 font-medium text-zinc-800">
                      {u.displayName || "—"}
                      {!u.isActive ? (
                        <span className="ms-1.5 text-[11px] font-normal text-zinc-400">
                          ({t("users.inactive")})
                        </span>
                      ) : null}
                    </td>
                    <td className="ltr-token px-3 py-2.5 text-zinc-600">{u.email}</td>
                    <td className="px-3 py-2.5">
                      <Badge tone={u.role === "Admin" ? "blue" : "zinc"}>{t(roleKey)}</Badge>
                    </td>
                    <td className="px-3 py-2.5">
                      <span
                        className={clsx(
                          "inline-flex items-center gap-1 text-xs font-medium",
                          u.isActive ? "text-emerald-600" : "text-zinc-400"
                        )}
                      >
                        {u.isActive ? <ShieldCheck size={13} /> : <ShieldOff size={13} />}
                        {u.isActive ? t("users.active") : t("users.inactive")}
                      </span>
                    </td>
                    <td className="px-3 py-2.5 text-xs text-zinc-400">
                      {formatDateTime(u.createdAt, lang)}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2.5 text-end">
                      <Button
                        variant="ghost"
                        size="sm"
                        aria-label={t("users.edit")}
                        title={t("users.edit")}
                        onClick={() => {
                          setEditTarget(u);
                          setEditDisplayName(u.displayName ?? "");
                          setEditRole(u.role);
                        }}
                      >
                        <Pencil size={14} />
                      </Button>
                      <Button
                        variant="ghost"
                        size="sm"
                        className={u.isActive ? "text-red-600 hover:bg-red-50" : ""}
                        disabled={toggleMut.isPending}
                        onClick={() => setToggleTarget(u)}
                        title={
                          u.isActive
                            ? t("users.deactivate")
                            : t("users.activate")
                        }
                      >
                        {u.isActive ? <ShieldOff size={14} /> : <ShieldCheck size={14} />}
                      </Button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </Card>
      )}

      {/* Create-user dialog */}
      <Dialog
        open={showCreate}
        onClose={() => setShowCreate(false)}
        title={t("users.createUser")}
        description={t("users.createHint")}
      >
        <form
          onSubmit={(ev) => {
            ev.preventDefault();
            createMut.mutate();
          }}
        >
          <div className="grid gap-3">
            <Field label={t("auth.email")} required htmlFor="nu-email">
              <Input
                id="nu-email"
                type="email"
                dir="ltr"
                autoComplete="off"
                required
                value={form.email}
                onChange={(e) => setForm({ ...form, email: e.target.value })}
              />
            </Field>
            <Field label={t("auth.password")} hint={t("users.passwordHint")} required htmlFor="nu-pass">
              <Input
                id="nu-pass"
                type="password"
                dir="ltr"
                autoComplete="new-password"
                minLength={8}
                required
                value={form.password}
                onChange={(e) => setForm({ ...form, password: e.target.value })}
              />
            </Field>
            <Field label={`${t("settings.profileName")} (${t("common.optional")})`} htmlFor="nu-name">
              <Input
                id="nu-name"
                dir="ltr"
                value={form.displayName}
                onChange={(e) => setForm({ ...form, displayName: e.target.value })}
              />
            </Field>
            <Field label={t("user.role")} required htmlFor="nu-role">
              <Select
                id="nu-role"
                value={form.role}
                onChange={(e) => setForm({ ...form, role: e.target.value as UserForm["role"] })}
              >
                {ROLES.map((r) => (
                  <option key={r} value={r}>
                    {t(`user.role${r}` as const)}
                  </option>
                ))}
              </Select>
            </Field>
          </div>
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

      {/* Edit display name / role */}
      <Dialog
        open={!!editTarget}
        onClose={() => setEditTarget(null)}
        title={t("users.edit")}
        description={editTarget?.email}
      >
        <form
          onSubmit={(ev) => {
            ev.preventDefault();
            editMut.mutate();
          }}
        >
          <div className="grid gap-3">
            <Field label={t("settings.profileName")} htmlFor="eu-name">
              <Input
                id="eu-name"
                dir="ltr"
                value={editDisplayName}
                onChange={(e) => setEditDisplayName(e.target.value)}
              />
            </Field>
            <Field
              label={t("user.role")}
              hint={
                editTarget?.role === "Admin" && editRole !== "Admin"
                  ? t("users.demoteAdminWarning")
                  : undefined
              }
              htmlFor="eu-role"
            >
              <Select
                id="eu-role"
                value={editRole}
                onChange={(e) => setEditRole(e.target.value)}
              >
                {ROLES.map((r) => (
                  <option key={r} value={r}>
                    {t(`user.role${r}` as const)}
                  </option>
                ))}
              </Select>
            </Field>
          </div>
          <div className="mt-5 flex justify-end gap-2">
            <Button type="button" variant="secondary" onClick={() => setEditTarget(null)}>
              {t("common.cancel")}
            </Button>
            <Button type="submit" loading={editMut.isPending}>
              {t("common.save")}
            </Button>
          </div>
        </form>
      </Dialog>

      {/* Activate / deactivate confirmation */}
      <ConfirmDialog
        open={!!toggleTarget}
        onClose={() => setToggleTarget(null)}
        onConfirm={() => toggleMut.mutate(toggleTarget!)}
        loading={toggleMut.isPending}
        title={
          toggleTarget?.isActive
            ? t("users.confirmDeactivateTitle", { name: toggleTarget?.displayName || toggleTarget?.email || "" })
            : t("users.confirmActivateTitle", { name: toggleTarget?.displayName || toggleTarget?.email || "" })
        }
        message={
          toggleTarget?.isActive
            ? t("users.confirmDeactivateMessage")
            : t("users.confirmActivateMessage")
        }
      />
    </>
  );
}
