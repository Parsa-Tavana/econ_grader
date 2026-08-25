import { useState, type FormEvent } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { LogIn, GraduationCap } from "lucide-react";
import { login, getAuthUser } from "../api/auth";
import { Button } from "../components/ui/Button";
import { Input } from "../components/ui/Input";

/** Route guard helper: redirect already-authed users away from /login. */
export function isLoggedIn(): boolean {
  return !!localStorage.getItem("econgrader.token");
}

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
  if (getAuthUser() && isLoggedIn()) {
    navigate(next, { replace: true });
    return null;
  }

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(email.trim(), password);
      navigate(next, { replace: true });
    } catch (err) {
      // Backend returns { code: "INVALID_CREDENTIALS", message } on bad login
      const data = (err as { response?: { data?: { code?: string; message?: string } } })
        .response?.data;
      setError(
        data?.code === "INVALID_CREDENTIALS"
          ? t("auth.invalidCredentials")
          : t("auth.loginFailed")
      );
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
      </div>
    </div>
  );
}
