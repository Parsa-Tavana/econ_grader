import axios from "axios";

/**
 * Base HTTP client for the EconGrader .NET API.
 * In dev, Vite proxies /api → http://localhost:8080 (see vite.config.ts).
 */
export const api = axios.create({
  baseURL: "/api",
  headers: { "Content-Type": "application/json" },
});

/**
 * JWT bearer auth — identity comes exclusively from the token's claims
 * (sub / role / email); the backend derives the acting user from the
 * validated JWT. No identity headers are sent.
 */

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Strict GUID check (used by filters and form validation). */
export function isValidGuid(value: string | null | undefined): boolean {
  return !!value && GUID_RE.test(value.trim());
}

/** Attach the JWT to every request; 401 → session cleared + login redirect. */
api.interceptors.request.use((config) => {
  const token = localStorage.getItem("econgrader.token");
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

api.interceptors.response.use(
  (resp) => resp,
  (error) => {
    if (axios.isAxiosError(error) && error.response?.status === 401) {
      const path = window.location.hash || window.location.pathname;
      localStorage.removeItem("econgrader.token");
      localStorage.removeItem("econgrader.user");
      // Avoid redirect loop when the failure IS the login attempt itself.
      if (!path.includes("/login")) {
        const here = encodeURIComponent(window.location.pathname + window.location.search);
        window.location.assign(`/login?next=${here}`);
      }
    }
    return Promise.reject(error);
  }
);

/** Extract a readable message from an Axios error / backend problem response.
 *  Prefers the stable error `code` (e.g. DUPLICATE_QUESTION_NUMBER) which can be
 *  translated by the UI layer via `friendlyError`. Falls back to the human message.
 */
export function apiErrorMessage(err: unknown): string {
  if (axios.isAxiosError(err)) {
    const data = err.response?.data as
      | { code?: string; detail?: string; message?: string; title?: string; errors?: unknown }
      | string
      | undefined;
    if (typeof data === "string") return data;
    // Prefer the stable code for i18n translation
    if (data?.code) return data.code;
    if (data?.detail) return data.detail;
    if (data?.message) return data.message;
    if (data?.title) {
      const errs = data.errors;
      if (errs && typeof errs === "object") {
        const first = Object.values(errs as Record<string, string[]>).flat()[0];
        if (first) return first;
      }
      return data.title;
    }
    if (err.code === "ERR_NETWORK") return "NETWORK_ERROR";
    return err.message;
  }
  if (err instanceof Error && err.message) return err.message;
  return String(err);
}
