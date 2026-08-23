using EconGrader.Application.Interfaces;
using EconGrader.Domain.Entities;

namespace EconGrader.Application.Services;

/// <summary>
/// Identity is currently attribution-only (trusted X-User-Id header, no auth).
/// Rows in Users are required by FK constraints (Exams.CreatedByUserId,
/// TeacherReviews.TeacherUserId), so any first-seen GUID is auto-provisioned
/// as a placeholder Teacher account. When real authentication is added this
/// becomes the user-creation path.
/// </summary>
public static class UserProvisioning
{
    public static async Task<User> EnsureUserAsync(this IAppDbContext db, Guid userId, CancellationToken ct = default)
    {
        var existing = await db.Users.FindAsync([userId], ct);
        if (existing is not null) return existing;

        var user = new User
        {
            Id = userId,
            Email = $"{userId}@local.econgrader",
            // No real authentication yet — hash column is NOT NULL; store a
            // sentinel that can never match a login attempt.
            PasswordHash = "external-identity",
            DisplayName = $"Teacher {userId.ToString()[..8]}",
            Role = UserRole.Teacher,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }
}