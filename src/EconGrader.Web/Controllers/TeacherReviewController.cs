using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EconGrader.Application.Services;
using EconGrader.Domain.Entities;
using EconGrader.Web.Services;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/grading/{runId:guid}/review")]
[Authorize]
public sealed class TeacherReviewController : ControllerBase
{
    private readonly ITeacherReviewService _svc;
    private readonly IAccessScopeService _scope;
    private readonly CurrentUser _user;

    public TeacherReviewController(ITeacherReviewService svc, IAccessScopeService scope, CurrentUser user)
    {
        _svc = svc;
        _scope = scope;
        _user = user;
    }

    /// <summary>Accept the AI score. Teachers + correctors on their scoped runs.</summary>
    [HttpPost("accept")]
    [Authorize(Roles = $"{nameof(UserRole.Teacher)},{nameof(UserRole.Corrector)}")]
    public async Task<IActionResult> Accept(
        Guid runId,
        [FromBody] ReviewAcceptRequest body,
        CancellationToken ct)
    {
        if (!await _scope.CanAccessRunAsync(_user, runId, ct)) return Forbid();
        var review = await _svc.AcceptAsync(runId, _user.UserId, body.Note, ct);
        // Same shape as GET history — entity + Teacher name resolved.
        return Ok((await _svc.GetHistoryAsync(runId, ct)).First(r => r.Id == review.Id));
    }

    /// <summary>Override the AI score. Teachers + correctors on their scoped runs.</summary>
    [HttpPost("override")]
    [Authorize(Roles = $"{nameof(UserRole.Teacher)},{nameof(UserRole.Corrector)}")]
    public async Task<IActionResult> Override(
        Guid runId,
        [FromBody] ReviewOverrideRequest body,
        CancellationToken ct)
    {
        if (!await _scope.CanAccessRunAsync(_user, runId, ct)) return Forbid();
        var review = await _svc.OverrideAsync(runId, _user.UserId, body.NewScore, body.Note, ct);
        return Ok((await _svc.GetHistoryAsync(runId, ct)).First(r => r.Id == review.Id));
    }

    /// <summary>Review history. Same role set as accept/override; students and
    /// admins use the run endpoints instead (history exposes reviewer identities).</summary>
    [HttpGet("history")]
    [Authorize(Roles = $"{nameof(UserRole.Teacher)},{nameof(UserRole.Corrector)}")]
    public async Task<IActionResult> History(Guid runId, CancellationToken ct)
    {
        if (!await _scope.CanAccessRunAsync(_user, runId, ct)) return Forbid();
        return Ok(await _svc.GetHistoryAsync(runId, ct));
    }
}

public record ReviewAcceptRequest(string? Note);
public record ReviewOverrideRequest(decimal NewScore, string? Note);
