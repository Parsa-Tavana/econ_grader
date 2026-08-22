using Microsoft.AspNetCore.Mvc;
using EconGrader.Application.Services;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/grading/{runId:guid}/review")]
public sealed class TeacherReviewController : ControllerBase
{
    private readonly ITeacherReviewService _svc;

    public TeacherReviewController(ITeacherReviewService svc) => _svc = svc;

    [HttpPost("accept")]
    public async Task<IActionResult> Accept(
        Guid runId,
        [FromBody] ReviewAcceptRequest body,
        [FromHeader(Name = "X-User-Id")] Guid teacherUserId,
        CancellationToken ct) =>
        Ok(await _svc.AcceptAsync(runId, teacherUserId, body.Note, ct));

    [HttpPost("override")]
    public async Task<IActionResult> Override(
        Guid runId,
        [FromBody] ReviewOverrideRequest body,
        [FromHeader(Name = "X-User-Id")] Guid teacherUserId,
        CancellationToken ct) =>
        Ok(await _svc.OverrideAsync(runId, teacherUserId, body.NewScore, body.Note, ct));

    [HttpGet("history")]
    public async Task<IActionResult> History(Guid runId, CancellationToken ct) =>
        Ok(await _svc.GetHistoryAsync(runId, ct));
}

public record ReviewAcceptRequest(string? Note);
public record ReviewOverrideRequest(decimal NewScore, string? Note);
