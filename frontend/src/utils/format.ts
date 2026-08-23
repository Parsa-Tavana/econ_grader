import type { AppLang } from "../i18n";

const FA_DIGITS = ["۰", "۱", "۲", "۳", "۴", "۵", "۶", "۷", "۸", "۹"];

export function toFaDigits(input: string): string {
  return input.replace(/\d/g, (d) => FA_DIGITS[Number(d)]);
}

export function formatNumber(
  value: number | null | undefined,
  lang: AppLang,
  opts?: Intl.NumberFormatOptions
): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "—";
  const s = new Intl.NumberFormat("en-US", {
    maximumFractionDigits: 2,
    ...opts,
  }).format(value);
  return lang === "fa" ? toFaDigits(s) : s;
}

export function formatScore(
  value: number | null | undefined,
  lang: AppLang
): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "—";
  return formatNumber(value, lang, { maximumFractionDigits: 2 });
}

export function formatPercent(value: number | null | undefined, lang: AppLang): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "—";
  return `${formatNumber(value, lang, { maximumFractionDigits: 1 })}٪`;
}

export function formatCost(usd: number | null | undefined, lang: AppLang): string {
  if (usd === null || usd === undefined) return "—";
  const s = `$${usd.toFixed(4)}`;
  return lang === "fa" ? toFaDigits(s) : s;
}

export function formatDateTime(iso: string | null | undefined, lang: AppLang): string {
  if (!iso) return "—";
  try {
    const d = new Date(iso);
    const s = new Intl.DateTimeFormat("en-GB", {
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
    }).format(d);
    return lang === "fa" ? faDateTime(s) : s;
  } catch {
    return iso;
  }
}

export function formatDate(iso: string | null | undefined, lang: AppLang): string {
  if (!iso) return "—";
  try {
    const d = new Date(iso);
    const s = new Intl.DateTimeFormat("en-GB", {
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
    }).format(d);
    return lang === "fa" ? toFaDigits(s) : s;
  } catch {
    return iso;
  }
}

/** Persian calendar date for the fa locale. */
export function faDateLong(iso: string | null | undefined): string {
  if (!iso) return "—";
  try {
    return new Intl.DateTimeFormat("fa-IR-u-ca-persian", {
      year: "numeric",
      month: "long",
      day: "numeric",
    }).format(new Date(iso));
  } catch {
    return iso;
  }
}

function faDateTime(enFormatted: string): string {
  return toFaDigits(enFormatted);
}

/** Relative time like "3 minutes ago" / «۳ دقیقه پیش» */
export function timeAgo(iso: string | null | undefined, lang: AppLang): string {
  if (!iso) return "—";
  const diffMs = Date.now() - new Date(iso).getTime();
  const rtf = new Intl.RelativeTimeFormat(lang === "fa" ? "fa" : "en", {
    numeric: "auto",
  });
  const mins = Math.round(-diffMs / 60000);
  if (Math.abs(mins) < 60) return rtf.format(mins, "minute");
  const hours = Math.round(mins / 60);
  if (Math.abs(hours) < 24) return rtf.format(hours, "hour");
  return rtf.format(Math.round(hours / 24), "day");
}

export function formatLatency(ms: number, lang: AppLang): string {
  const s = ms >= 1000 ? `${(ms / 1000).toFixed(1)}s` : `${ms}ms`;
  return lang === "fa" ? toFaDigits(s) : s;
}
