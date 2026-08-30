import type { AuthUser } from "../api/auth";

/**
 * Role helpers mirroring the backend's [Authorize(Roles = ...)] values
 * ("Teacher" | "Admin" | "Corrector" | "Student"). Comparison is
 * case-insensitive so UI code can pass plain strings safely.
 */

/** True when the signed-in user's role is one of `allowed`. */
export function hasRole(
  user: AuthUser | null | undefined,
  ...allowed: string[]
): boolean {
  if (!user?.isActive) return false;
  return allowed.some((r) => r.toLowerCase() === user.role.toLowerCase());
}

/** Admin-only convenience — mirrors backend nameof(UserRole.Admin) gates. */
export function isAdmin(user: AuthUser | null | undefined): boolean {
  return hasRole(user, "Admin");
}
