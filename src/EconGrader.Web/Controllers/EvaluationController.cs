using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EconGrader.Application.Evaluation;
using EconGrader.Domain.Entities;
using EconGrader.Web.Services;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class EvaluationController : ControllerBase
{
    private readonly EvaluationService _svc;
    private readonly IAccessScopeService _scope;
    private readonly CurrentUser _user;

    public EvaluationController(EvaluationService svc, IAccessScopeService scope, CurrentUser user)
    {
        _svc = svc;
        _scope = scope;
        _user = user;
    }

    /// <summary>
    /// Agreement metrics (MAE, RMSE, exact/within-0.5/within-1 %, bias,
    /// Pearson r, QWK) between AI scores and teacher ground truth for a question.
    /// Teachers on their own exams + admins only — correctors are kept out of
    /// agreement analytics by design.
    /// </summary>
    [HttpGet("question/{questionId:guid}")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<IActionResult> ForQuestion(
        Guid questionId,
        [FromQuery] string? provider,
        [FromQuery] string? modelName,
        CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, questionId, writeAccess: false, ct)) return Forbid();
        return Ok(await _svc.ForQuestionAsync(questionId, provider, modelName, ct));
    }

    /// <summary>Same metrics rolled up across every question of an exam.</summary>
    [HttpGet("exam/{examId:guid}")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<IActionResult> Overall(Guid examId, CancellationToken ct)
    {
        await _scope.AssertExamAccessAsync(_user, examId, writeAccess: false, ct);
        return Ok(await _svc.OverallAsync(examId, ct));
    }
}
