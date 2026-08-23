import { clsx } from "clsx";
import { AlertTriangle, Inbox, Loader2, RefreshCw } from "lucide-react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Button } from "./Button";

export function Spinner({ className }: { className?: string }) {
  return <Loader2 className={clsx("animate-spin text-primary-500", className ?? "h-6 w-6")} />;
}

export function LoadingBlock({ label }: { label?: string }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col items-center justify-center gap-3 py-16 text-zinc-500">
      <Spinner />
      <p className="text-sm">{label ?? t("common.loading")}</p>
    </div>
  );
}

export function EmptyState({
  title,
  hint,
  action,
}: {
  title: string;
  hint?: string;
  action?: ReactNode;
}) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 py-14 text-center">
      <Inbox className="h-10 w-10 text-zinc-300" />
      <p className="mt-1 font-medium text-zinc-700">{title}</p>
      {hint ? <p className="max-w-sm text-sm text-zinc-400">{hint}</p> : null}
      {action ? <div className="mt-3">{action}</div> : null}
    </div>
  );
}

export function ErrorState({
  message,
  onRetry,
}: {
  message: string;
  onRetry?: () => void;
}) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col items-center justify-center gap-3 py-14 text-center">
      <AlertTriangle className="h-10 w-10 text-red-300" />
      <p className="max-w-md text-sm text-red-600">{message}</p>
      {onRetry ? (
        <Button variant="secondary" size="sm" onClick={onRetry}>
          <RefreshCw size={14} />
          {t("common.retry")}
        </Button>
      ) : null}
    </div>
  );
}

/** Maps query errors to a translated, human-friendly message. */
export function friendlyError(err: unknown, t: (k: string) => string): string {
  const raw = err instanceof Error ? err.message : String(err);
  if (raw === "NETWORK_ERROR") return t("common.networkError");
  return raw || t("states.errorOccurred");
}
