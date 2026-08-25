import { api } from "./client";

/**
 * JWT authentication against POST /api/auth/login (see AuthController).
 * The access token is stored in localStorage and attached by the
 * request interceptor in client.ts. Identity NEVER travels via headers —
 * the backend derives it from the token's claims.
 */

export interface AuthUser {
  id: string;
  email: string;
  displayName: string;
  /** "Teacher" | "Admin" | "Corrector" | "Student" */
  role: string;
  isActive: boolean;
  createdAt: string;
}

export interface LoginResponse {
  accessToken: string;
  tokenType: string;
  expiresInSeconds: number;
  user: AuthUser;
}

const TOKEN_KEY = "econgrader.token";
const USER_KEY = "econgrader.user";

export function getToken(): string | null {
  return localStorage.getItem(TOKEN_KEY);
}

export function getAuthUser(): AuthUser | null {
  const raw = localStorage.getItem(USER_KEY);
  if (!raw) return null;
  try {
    return JSON.parse(raw) as AuthUser;
  } catch {
    localStorage.removeItem(USER_KEY);
    return null;
  }
}

export function storeSession(login: LoginResponse) {
  localStorage.setItem(TOKEN_KEY, login.accessToken);
  localStorage.setItem(USER_KEY, JSON.stringify(login.user));
}

export function clearSession() {
  localStorage.removeItem(TOKEN_KEY);
  localStorage.removeItem(USER_KEY);
}

/** Email + password → session (token + user). Throws on invalid credentials. */
export async function login(email: string, password: string): Promise<LoginResponse> {
  const resp = await api.post<LoginResponse>("/auth/login", { email, password });
  storeSession(resp.data);
  return resp.data;
}

export function logout() {
  clearSession();
}
