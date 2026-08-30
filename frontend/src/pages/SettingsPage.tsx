import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { clsx } from "clsx";
import { healthCheck } from "../api/system";
import { getAuthUser } from "../api/auth";
import { changeLanguage, type AppLang } from "../i18n";
import { PageHeader, Card, CardHeader, Badge, Button } from "../components/ui";

export default function SettingsPage() {
  const { t, i18n } = useTranslation();
  const current = (i18n.language?.startsWith("fa") ? "fa" : "en") as AppLang;
  const user = getAuthUser();

  // health
  const [healthState, setHealthState] = useState<"loading" | "up" | "down">("loading");
  const [gradingUp, setGradingUp] = useState<boolean | null>(null);

  async function refreshHealth() {
    setHealthState("loading");
    try {
      const h = await healthCheck();
      setHealthState(h.status ? "up" : "down");
      setGradingUp(h.dependencies.gradingService.up);
    } catch {
      setHealthState("down");
      setGradingUp(false);
    }
  }

  useEffect(() => {
    void refreshHealth();
  }, []);

  return (
    <>
      <PageHeader title={t("settings.title")} />

      <div className="grid gap-4 lg:grid-cols-2">
        {/* Language */}
        <Card>
          <CardHeader title={t("settings.language")} subtitle={t("settings.languageHint")} />
          <div className="flex gap-2">
            <Button
              variant={current === "fa" ? "primary" : "secondary"}
              onClick={() => changeLanguage("fa")}
            >
              {t("settings.languageFa")}
            </Button>
            <Button
              variant={current === "en" ? "primary" : "secondary"}
              onClick={() => changeLanguage("en")}
            >
              {t("settings.languageEn")}
            </Button>
          </div>
          <p className="mt-3 text-[11px] text-zinc-400">
            {t("common.status")}:{" "}
            <span className="ltr-token">{document.documentElement.dir.toUpperCase()}</span> · fa-IR / en-US
          </p>
        </Card>

        {/* Profile (read-only — identity comes from the JWT session) */}
        <Card>
          <CardHeader title={t("settings.profile")} subtitle={t("settings.profileHint")} />
          {user ? (
            <dl className="space-y-3 text-sm">
              <div className="flex items-center justify-between gap-3">
                <dt className="text-xs text-zinc-500">{t("settings.profileName")}</dt>
                <dd className="font-medium text-zinc-900">{user.displayName || "—"}</dd>
              </div>
              <div className="flex items-center justify-between gap-3">
                <dt className="text-xs text-zinc-500">{t("auth.email")}</dt>
                <dd className="ltr-token font-medium text-zinc-900">{user.email}</dd>
              </div>
              <div className="flex items-center justify-between gap-3">
                <dt className="text-xs text-zinc-500">{t("user.role")}</dt>
                <dd>
                  <Badge tone="blue">{t(`user.role${user.role}` as const)}</Badge>
                </dd>
              </div>
            </dl>
          ) : (
            <p className="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700">
              {t("settings.profileSignedOut")}
            </p>
          )}
        </Card>

        {/* System health */}
        <Card className="lg:col-span-2">
          <CardHeader
            title={t("settings.systemHealth")}
            action={
              <Button size="sm" variant="ghost" onClick={() => void refreshHealth()}>
                {t("common.refresh")}
              </Button>
            }
          />
          <div className="flex flex-wrap items-center gap-3">
            <StatusPill
              label={t("settings.apiStatus")}
              state={healthState === "loading" ? "loading" : healthState === "up" ? "ok" : "bad"}
              okText={t("settings.online")}
              badText={t("settings.offline")}
              loadingText={t("settings.checkingHealth")}
            />
            <StatusPill
              label={t("settings.gradingServiceStatus")}
              state={gradingUp === null ? "loading" : gradingUp ? "ok" : "bad"}
              okText={t("settings.online")}
              badText={t("settings.offline")}
              loadingText={t("settings.checkingHealth")}
            />
          </div>
          <p className="mt-4 rounded-xl bg-zinc-50 p-3 text-[11px] leading-relaxed text-zinc-500 ltr-token">
            GET /api/health → {"{ status, service, dependencies.gradingService.up }"}
          </p>
        </Card>
      </div>
    </>
  );
}

function StatusPill({
  label,
  state,
  okText,
  badText,
  loadingText,
}: {
  label: string;
  state: "ok" | "bad" | "loading";
  okText: string;
  badText: string;
  loadingText: string;
}) {
  return (
    <div
      className={clsx(
        "flex min-w-[180px] flex-1 items-center justify-between gap-3 rounded-xl border px-4 py-3",
        state === "ok" && "border-emerald-200 bg-emerald-50",
        state === "bad" && "border-red-200 bg-red-50",
        state === "loading" && "border-zinc-200 bg-zinc-50"
      )}
    >
      <span className="text-xs font-medium text-zinc-600">{label}</span>
      <Badge tone={state === "ok" ? "green" : state === "bad" ? "red" : "zinc"}>
        {state === "ok" ? okText : state === "bad" ? badText : loadingText}
      </Badge>
    </div>
  );
}
