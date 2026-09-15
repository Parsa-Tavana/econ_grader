using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EconGrader.Application.Evaluation;
using EconGrader.Application.Services;
using EconGrader.Domain.Entities;
using EconGrader.Web.Services;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class EvaluationController : ControllerBase
{
    private readonly EvaluationService _svc;
    private readonly IGoldenSetService _goldenSet;
    private readonly IAccessScopeService _scope;
    private readonly CurrentUser _user;

    public EvaluationController(EvaluationService svc, IGoldenSetService goldenSet,
        IAccessScopeService scope, CurrentUser user)
    {
        _svc = svc;
        _goldenSet = goldenSet;
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

    /// <summary>Golden-set harness (M4): re-grade the frozen teacher-scored
    /// set of a question under a candidate config and diff QWK/MAE/exact vs
    /// the persisted baseline. Regressed=true means do NOT roll out.</summary>
    [HttpPost("golden-set")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<IActionResult> RunGoldenSet(
        [FromBody] GoldenSetRequest request, CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, request.QuestionId, writeAccess: true, ct))
            return Forbid();
        var result = await _goldenSet.RunGoldenSetAsync(request, _user.UserId, ct);
        return Ok(result);
    }

    /// <summary>Harness history (M4): past golden-set runs, newest first.</summary>
    [HttpGet("golden-set/history")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<IActionResult> GoldenSetHistory(
        [FromQuery] Guid? questionId, CancellationToken ct)
    {
        if (questionId is { } qid &&
            !await _scope.CanAccessQuestionAsync(_user, qid, writeAccess: false, ct))
            return Forbid();
        return Ok(await _goldenSet.ListEvalRunsAsync(questionId, ct));
    }
}
