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

/**
 * Stable backend error codes → i18n keys. Addresses the EN-only leak caveat:
 * server validation errors (`DUPLICATE_QUESTION_NUMBER`, …) now surface in the
 * active locale instead of raw English.
 */
export const errorCodeToKey: Record<string, string> = {
  NETWORK_ERROR: "common.networkError",
  DUPLICATE_QUESTION_NUMBER: "errors.duplicateQuestionNumber",
  EMPTY_CRITERIA: "errors.emptyCriteria",
  SCORE_EXCEEDS_MAX: "errors.scoreExceedsMax",
  INVALID_SCORE: "errors.invalidScore",
  EMAIL_TAKEN: "errors.emailTaken",
  LAST_ADMIN: "errors.lastAdmin",
  UNSUPPORTED_MEDIA_TYPE: "errors.unsupportedFileType",
  DUPLICATE_STUDENT_EXTERNAL_ID: "errors.duplicateStudentId",
  VALIDATION_ERROR: "errors.validationError",
  BUSINESS_RULE_VIOLATION: "errors.businessRuleViolation",
  NOT_FOUND: "states.errorOccurred",
  FORBIDDEN: "errors.forbidden",
  INTERNAL_ERROR: "states.errorOccurred",
  STORAGE_ACCESS_DENIED: "errors.storageAccessDenied",
  STORAGE_UNAVAILABLE: "errors.storageUnavailable",
  DEPENDENCY_UNAVAILABLE: "errors.dependencyUnavailable",
  TIMEOUT: "errors.timeout",
};

/** Maps query/mutation errors to a translated, human-friendly message.
 *  Accepts either a raw AxiosError, a pre-extracted message string, or a Error.
 */
export function friendlyError(err: unknown, t: (k: string) => string): string {
  // Pull the backend `code` out of an AxiosError shape (pages pass the raw error).
  if (err && typeof err === "object" && "response" in err) {
    const data = (err as { response?: { data?: { code?: string; message?: string } } }).response?.data;
    if (data?.code && errorCodeToKey[data.code]) return t(errorCodeToKey[data.code]);
    if (data?.message) return data.message;
  }

  const raw = err instanceof Error ? err.message : typeof err === "string" ? err : String(err);
  if (errorCodeToKey[raw]) return t(errorCodeToKey[raw]);
  return raw || t("states.errorOccurred");
}

/**
 * Extracts the backend correlation ID from an axios error response
 * (body.traceId or X-Correlation-Id header) so the UI can show it and a
 * developer can grep the server logs for that exact ID.
 */
export function traceIdOf(err: unknown): string | null {
  const e = err as { response?: { data?: { traceId?: string }; headers?: Record<string, string> } } | null;
  return e?.response?.data?.traceId ?? e?.response?.headers?.["x-correlation-id"] ?? null;
}
