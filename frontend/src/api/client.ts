import axios from "axios";

/**
 * Base HTTP client for the EconGrader .NET API.
 * In dev, Vite proxies /api → http://localhost:8080 (see vite.config.ts).
 */
export const api = axios.create({
  baseURL: "/api",
  headers: { "Content-Type": "application/json" },
});

/** Identity is a trusted X-User-Id GUID header (attribution only — see PROJECT_MAP §8). */
let currentUserId: string | null = localStorage.getItem("econgrader.userId");

export function setUserId(id: string | null) {
  currentUserId = id;
  if (id) localStorage.setItem("econgrader.userId", id);
  else localStorage.removeItem("econgrader.userId");
}

export function getUserId(): string | null {
  return currentUserId;
}

api.interceptors.request.use((config) => {
  if (currentUserId) config.headers["X-User-Id"] = currentUserId;
  return config;
});

/** Extract a readable message from an Axios error / backend problem response. */
export function apiErrorMessage(err: unknown): string {
  if (axios.isAxiosError(err)) {
    const data = err.response?.data as { title?: string; detail?: string; errors?: unknown } | string | undefined;
    if (typeof data === "string") return data;
    if (data?.detail) return data.detail;
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
  return String(err);
}