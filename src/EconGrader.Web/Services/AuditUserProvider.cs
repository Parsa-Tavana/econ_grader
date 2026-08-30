using EconGrader.Application.Interfaces;

namespace EconGrader.Web.Services;

/// <summary>
/// Resolves the acting user for audit attribution from the validated JWT
/// (via the same claims CurrentUser parses) — never from headers.
/// Returns null when there is no authenticated request (e.g. background jobs).
/// </summary>
public sealed class AuditUserProvider(IHttpContextAccessor accessor) : IAuditUserProvider
{
    public Guid? CurrentUserId
    {
        get
        {
            var user = accessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true) return null;
            return CurrentUser.From(user)?.UserId;
        }
    }
}
