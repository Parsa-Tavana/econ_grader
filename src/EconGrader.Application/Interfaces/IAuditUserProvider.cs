namespace EconGrader.Application.Interfaces;

/// <summary>
/// Supplies the authenticated actor for audit rows when a service call site
/// has no user id of its own. Implemented in Web from the validated JWT
/// claims (never headers); returns null outside an authenticated request.
/// </summary>
public interface IAuditUserProvider
{
    Guid? CurrentUserId { get; }
}
