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
/** Epoch ms when the access token expires (from LoginResponse.expiresInSeconds). */
const EXPIRY_KEY = "econgrader.tokenExpiresAt";

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
  localStorage.setItem(EXPIRY_KEY, String(Date.now() + login.expiresInSeconds * 1000));
}

export function clearSession() {
  localStorage.removeItem(TOKEN_KEY);
  localStorage.removeItem(USER_KEY);
  localStorage.removeItem(EXPIRY_KEY);
}

/**
 * True only while the stored token is present AND unexpired. An expired
 * session is cleared on check so guards send the user straight to /login.
 * The axios 401 handler in client.ts stays as a backstop for tokens the
 * server rejects early (revoked / signing-key rotation).
 */
export function isLoggedIn(): boolean {
  const token = localStorage.getItem(TOKEN_KEY);
  if (!token) return false;
  const expiresAt = Number(localStorage.getItem(EXPIRY_KEY));
  if (!Number.isFinite(expiresAt)) return true; // legacy session without stamp — defer to server
  if (Date.now() >= expiresAt) {
    clearSession();
    return false;
  }
  return true;
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

// ── Admin user management + first-run bootstrap ─────────────────────────────

/** Same shape as AuthUser — returned by GET/POST /auth/users and PUT /auth/users/{id}. */
export type ManagedUser = AuthUser;

export interface CreateUserRequest {
  email: string;
  password: string;
  displayName: string;
  /** "Teacher" | "Admin" | "Corrector" | "Student" */
  role: string;
}

export interface UpdateUserRequest {
  isActive?: boolean;
  displayName?: string;
  role?: string;
}

export interface BootstrapAdminRequest {
  bootstrapKey: string;
  email: string;
  password: string;
  displayName?: string;
}

/** One-shot first-admin creation. 403 BOOTSTRAP_DISABLED/CLOSED, 401 INVALID_CREDENTIALS (bad key), 409 EMAIL_TAKEN. */
export async function bootstrapAdmin(req: BootstrapAdminRequest): Promise<LoginResponse> {
  const resp = await api.post<LoginResponse>("/auth/bootstrap-admin", req);
  storeSession(resp.data);
  return resp.data;
}

/** [Authorize(Roles="Admin")] from here down. */
export async function listUsers(): Promise<ManagedUser[]> {
  const resp = await api.get<ManagedUser[]>("/auth/users");
  return resp.data;
}

export async function createUser(req: CreateUserRequest): Promise<ManagedUser> {
  const resp = await api.post<ManagedUser>("/auth/users", req);
  return resp.data;
}

export async function updateUser(id: string, req: UpdateUserRequest): Promise<ManagedUser> {
  const resp = await api.put<ManagedUser>(`/auth/users/${id}`, req);
  return resp.data;
}
