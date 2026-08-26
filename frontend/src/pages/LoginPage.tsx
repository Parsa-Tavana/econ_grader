import { useState, useEffect, type FormEvent } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { LogIn, GraduationCap, ChevronDown, ChevronUp, UserPlus } from "lucide-react";
import {
  login,
  bootstrapAdmin,
  getAuthUser,
  isLoggedIn,
  type LoginResponse,
} from "../api/auth";
import { Button } from "../components/ui/Button";
import { Input } from "../components/ui/Input";

export default function LoginPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const next = params.get("next") || "/";

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Already signed in? Straight to the app.
  // (useEffect, not render-time navigate — navigating during render warns
  // and can abort the current component tree mid-render.)
  const authed = getAuthUser() !== null && isLoggedIn();
  useEffect(() => {
    if (authed) navigate(next, { replace: true });
  }, [authed, next, navigate]);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(email.trim(), password);
      navigate(next, { replace: true });
    } catch (err) {
      setError(loginErrorMessage(err, t));
      setBusy(false);
    }
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-zinc-50 px-4">
      <div className="w-full max-w-sm">
        <div className="mb-6 flex flex-col items-center gap-2 text-center">
          <div className="flex h-12 w-12 items-center justify-center rounded-2xl bg-primary-600 text-white">
            <GraduationCap size={26} />
          </div>
          <h1 className="text-xl font-bold text-zinc-900">{t("app.name")}</h1>
          <p className="text-xs text-zinc-500">{t("app.tagline")}</p>
        </div>

        <form
          onSubmit={handleSubmit}
          className="app-card space-y-4 p-6 shadow-sm"
        >
          <div>
            <label htmlFor="email" className="mb-1 block text-xs font-medium text-zinc-700">
              {t("auth.email")}
            </label>
            <Input
              id="email"
              type="email"
              dir="ltr"
              autoComplete="username"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="admin@local.econgrader"
            />
          </div>
          <div>
            <label htmlFor="password" className="mb-1 block text-xs font-medium text-zinc-700">
              {t("auth.password")}
            </label>
            <Input
              id="password"
              type="password"
              dir="ltr"
              autoComplete="current-password"
              required
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
          </div>

          {error && (
            <p className="rounded-lg bg-red-50 px-3 py-2 text-xs font-medium text-red-600">
              {error}
            </p>
          )}

          <Button type="submit" className="w-full" loading={busy}>
            <LogIn size={16} />
            {t("auth.signIn")}
          </Button>
        </form>

        <FirstRunSetup onBootstrapped={() => navigate(next, { replace: true })} />
      </div>
    </div>
  );
}

/** Maps auth endpoint error codes → translated message (shared by login + bootstrap). */
function loginErrorMessage(
  err: unknown,
  t: (k: string) => string,
): string {
  const code = (err as { response?: { data?: { code?: string } } })?.response?.data?.code;
  switch (code) {
    case "INVALID_CREDENTIALS":
      return t("auth.invalidCredentials");
    case "BOOTSTRAP_DISABLED":
      return t("auth.bootstrapDisabled");
    case "BOOTSTRAP_CLOSED":
      return t("auth.bootstrapClosed");
    case "EMAIL_TAKEN":
      return t("auth.emailTaken");
    default:
      return t("auth.loginFailed");
  }
}

/**
 * First-run admin bootstrap. Collapsed by default and never probed
 * automatically — the backend answers BOOTSTRAP_DISABLED/CLOSED only when
 * explicitly called, so we surface those as friendly messages here.
 */
function FirstRunSetup({ onBootstrapped }: { onBootstrapped: () => void }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [bootstrapKey, setBootstrapKey] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function handleBootstrap(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const session: LoginResponse = await bootstrapAdmin({
        bootstrapKey: bootstrapKey.trim(),
        email: email.trim(),
        password,
        displayName: displayName.trim() || undefined,
      });
      setBusy(false);
      if (session.user.role !== "Admin") {
        setError(t("auth.bootstrapClosed"));
        return;
      }
      onBootstrapped();
    } catch (err) {
      setError(loginErrorMessage(err, t));
      setBusy(false);
    }
  }

  return (
    <div className="mt-4">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        className="flex w-full items-center justify-center gap-1.5 rounded-lg px-3 py-2 text-xs font-medium text-zinc-500 transition hover:bg-zinc-100 hover:text-zinc-700"
      >
        {open ? <ChevronUp size={14} /> : <ChevronDown size={14} />}
        {t("auth.bootstrapTitle")}
      </button>

      {open && (
        <form onSubmit={handleBootstrap} className="app-card mt-2 space-y-3 p-5">
          <p className="rounded-lg bg-zinc-50 px-3 py-2 text-[11px] leading-relaxed text-zinc-500">
            {t("auth.bootstrapHint")}
          </p>
          <Input
            type="password"
            dir="ltr"
            autoComplete="off"
            required
            value={bootstrapKey}
            onChange={(e) => setBootstrapKey(e.target.value)}
            placeholder={t("auth.bootstrapKeyPlaceholder")}
          />
          <Input
            type="email"
            dir="ltr"
            autoComplete="username"
            required
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder={t("auth.bootstrapEmailPlaceholder")}
          />
          <Input
            type="password"
            dir="ltr"
            autoComplete="new-password"
            required
            minLength={8}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            placeholder={t("auth.bootstrapPasswordPlaceholder")}
          />
          <Input
            dir="ltr"
            autoComplete="off"
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            placeholder={`${t("settings.profileName")} (${t("common.optional")})`}
          />

          {error && (
            <p className="rounded-lg bg-red-50 px-3 py-2 text-xs font-medium text-red-600">
              {error}
            </p>
          )}

          <Button type="submit" variant="secondary" className="w-full" loading={busy}>
            <UserPlus size={16} />
            {t("auth.bootstrapSubmit")}
          </Button>
        </form>
      )}
    </div>
  );
}
