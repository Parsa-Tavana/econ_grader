using System.Security.Claims;
using EconGrader.Domain.Entities;

namespace EconGrader.Web.Services;

/// <summary>Resolved identity of the authenticated caller, taken ONLY from the
/// validated JWT principal (never from headers). Registered scoped.</summary>
public sealed class CurrentUser
{
    public Guid UserId { get; init; }
    public UserRole Role { get; init; }
    /// <summary>Student-row id when the caller is a logged-in student; null otherwise.</summary>
    public Guid? StudentId { get; init; }
    public string Email { get; init; } = "";

    public bool IsAdmin => Role == UserRole.Admin;
    public bool IsTeacher => Role == UserRole.Teacher;
    public bool IsCorrector => Role == UserRole.Corrector;
    public bool IsStudent => Role == UserRole.Student;

    public static CurrentUser? From(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return null;
        if (!Enum.TryParse<UserRole>(principal.FindFirstValue(ClaimTypes.Role), out var role))
            return null;
        Guid? studentId = Guid.TryParse(principal.FindFirstValue("studentId"), out var sid) ? sid : null;
        return new CurrentUser
        {
            UserId = userId,
            Role = role,
            StudentId = studentId,
            Email = principal.FindFirstValue(ClaimTypes.Email) ?? "",
        };
    }
}

/// <summary>Throws ForbiddenException (→403) when an ownership/scope check fails.
/// Distinct from role-based 403 so handlers can fail with precise messages.</summary>
public sealed class ResourceAccessDeniedException(string message)
    : EconGrader.Application.Exceptions.DomainException(message, 403, "FORBIDDEN");
