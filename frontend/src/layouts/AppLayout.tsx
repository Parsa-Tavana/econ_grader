import { useEffect, useState } from "react";
import { NavLink, Outlet, useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import {
  LayoutDashboard,
  BookOpen,
  GraduationCap,
  PenLine,
  ChartLine,
  ScrollText,
  Settings,
  Users,
  Languages,
  Wifi,
  WifiOff,
  LogOut,
} from "lucide-react";
import { clsx } from "clsx";
import { changeLanguage, type AppLang } from "../i18n";
import { healthCheck } from "../api/system";
import { getAuthUser, logout, type AuthUser } from "../api/auth";
import { hasRole } from "../utils/roles";
import { ToastProvider } from "../hooks/useToast";

type NavItem = {
  to: string;
  key: string;
  icon: typeof LayoutDashboard;
  end?: boolean;
  /** When set, the entry renders only for these roles (mirrors backend [Authorize(Roles=...)]). */
  roles?: string[];
};

const NAV_ITEMS: NavItem[] = [
  { to: "/", key: "nav.dashboard", icon: LayoutDashboard, end: true },
  { to: "/exams", key: "nav.exams", icon: BookOpen },
  { to: "/students", key: "nav.students", icon: GraduationCap },
  { to: "/grading/queue", key: "nav.grading", icon: PenLine },
  { to: "/evaluation", key: "nav.evaluation", icon: ChartLine },
  // Audit log + user management are admin-only server-side ([Authorize(Roles="Admin")]).
  { to: "/audit", key: "nav.audit", icon: ScrollText, roles: ["Admin"] },
  { to: "/users", key: "nav.users", icon: Users, roles: ["Admin"] },
  { to: "/settings", key: "nav.settings", icon: Settings },
];

/** Role filter shared by the desktop sidebar and the mobile top-nav. */
function canSee(user: AuthUser | null, item: NavItem): boolean {
  return !item.roles || hasRole(user, ...item.roles);
}

function LanguageToggle() {
  const { i18n, t } = useTranslation();
  const current = (i18n.language?.startsWith("fa") ? "fa" : "en") as AppLang;
  const next: AppLang = current === "fa" ? "en" : "fa";
  return (
    <button
      onClick={() => changeLanguage(next)}
      className="inline-flex items-center gap-1.5 rounded-lg border border-zinc-200 px-2.5 py-1.5 text-xs font-medium text-zinc-600 transition hover:bg-zinc-100"
      title={t("settings.language")}
    >
      <Languages size={14} />
      <span className="ltr-token">{next === "fa" ? "فا" : "EN"}</span>
    </button>
  );
}

function HealthDot() {
  const { t } = useTranslation();
  const [up, setUp] = useState<boolean | null>(null);

  useEffect(() => {
    let alive = true;
    const check = () =>
      healthCheck()
        .then((h) => alive && setUp(h.dependencies.gradingService.up))
        .catch(() => alive && setUp(false));
    check();
    const id = window.setInterval(check, 30_000);
    return () => {
      alive = false;
      window.clearInterval(id);
    };
  }, []);

  return (
    <span
      className={clsx(
        "inline-flex items-center gap-1.5 rounded-lg border px-2.5 py-1.5 text-xs font-medium",
        up === null && "border-zinc-200 text-zinc-400",
        up === true && "border-emerald-200 bg-emerald-50 text-emerald-700",
        up === false && "border-red-200 bg-red-50 text-red-600"
      )}
      title={t("settings.gradingServiceStatus")}
    >
      {up === false ? <WifiOff size={13} /> : <Wifi size={13} />}
      {up === null
        ? t("settings.checkingHealth")
        : up
          ? t("settings.online")
          : t("settings.offline")}
    </span>
  );
}

function UserMenu() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const user = getAuthUser();
  if (!user) return null;
  const roleKey = `user.role${user.role}` as const;

  return (
    <div className="flex items-center gap-2">
      <span
        className="hidden max-w-[180px] truncate rounded-lg border border-zinc-200 px-2.5 py-1.5 text-xs font-medium text-zinc-600 sm:inline-flex"
        title={`${t("user.signedInAs")}: ${user.email}`}
      >
        {user.displayName || user.email}
        <span className="ms-1.5 text-zinc-400">· {t(roleKey)}</span>
      </span>
      <button
        onClick={() => {
          logout();
          navigate("/login", { replace: true });
        }}
        className="inline-flex items-center gap-1.5 rounded-lg border border-zinc-200 px-2.5 py-1.5 text-xs font-medium text-zinc-600 transition hover:bg-zinc-100"
        title={t("user.logout")}
      >
        <LogOut size={14} />
      </button>
    </div>
  );
}

export default function AppLayout() {
  const { t } = useTranslation();
  const user = getAuthUser();
  const visibleNav = NAV_ITEMS.filter((item) => canSee(user, item));

  return (
    <ToastProvider>
      <div className="flex min-h-screen">
        {/* Sidebar */}
        <aside className="fixed inset-y-0 z-40 hidden w-56 flex-col border-e border-zinc-200 bg-white md:flex rtl:right-0 ltr:left-0">
          <div className="flex h-16 items-center gap-2.5 border-b border-zinc-100 px-5">
            <div className="flex h-8 w-8 items-center justify-center rounded-xl bg-primary-600 text-sm font-bold text-white">
              EG
            </div>
            <div>
              <p className="text-sm font-bold leading-tight text-zinc-900">
                {t("app.name")}
              </p>
              <p className="text-[10px] text-zinc-400">{t("app.tagline")}</p>
            </div>
          </div>
          <nav className="flex flex-1 flex-col gap-0.5 overflow-y-auto p-3">
            {visibleNav.map(({ to, key, icon: Icon, end }) => (
              <NavLink
                key={to}
                to={to}
                end={end}
                className={({ isActive }) =>
                  clsx(
                    "flex items-center gap-2.5 rounded-xl px-3 py-2 text-sm font-medium transition-colors",
                    isActive
                      ? "bg-primary-50 text-primary-700"
                      : "text-zinc-600 hover:bg-zinc-50 hover:text-zinc-900"
                  )
                }
              >
                <Icon size={17} className="shrink-0" />
                {t(key)}
              </NavLink>
            ))}
          </nav>
        </aside>

        {/* Main */}
        <div className="flex min-h-screen flex-1 flex-col md:ms-56">
          <header className="sticky top-0 z-30 flex h-14 items-center justify-between gap-3 border-b border-zinc-200 bg-white/90 px-4 backdrop-blur md:px-6">
            <nav className="-mx-1 flex items-center gap-1 overflow-x-auto md:hidden">
              {visibleNav.slice(0, 4).map(({ to, key, end }) => (
                <NavLink
                  key={to}
                  to={to}
                  end={end}
                  className={({ isActive }) =>
                    clsx(
                      "rounded-lg px-2.5 py-1.5 text-xs font-medium whitespace-nowrap",
                      isActive ? "bg-primary-50 text-primary-700" : "text-zinc-500"
                    )
                  }
                >
                  {t(key)}
                </NavLink>
              ))}
            </nav>
            <div className="hidden md:block" />
            <div className="flex items-center gap-2">
              <HealthDot />
              <LanguageToggle />
              <UserMenu />
            </div>
          </header>

          <main className="flex-1 px-4 py-6 md:px-6 lg:px-8">
            <Outlet />
          </main>

          <footer className="border-t border-zinc-100 px-6 py-3 text-center text-[11px] text-zinc-400">
            EconGrader · AI-assisted grading · teacher scores never sent to the AI service
          </footer>
        </div>
      </div>
    </ToastProvider>
  );
}
