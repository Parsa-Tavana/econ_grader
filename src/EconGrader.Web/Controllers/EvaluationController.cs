using Microsoft.AspNetCore.Mvc;
using EconGrader.Application.Evaluation;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class EvaluationController : ControllerBase
{
    private readonly EvaluationService _svc;

    public EvaluationController(EvaluationService svc) => _svc = svc;

    /// <summary>
    /// Agreement metrics (MAE, RMSE, exact/within-0.5/within-1 %, bias,
    /// Pearson r, QWK) between AI scores and teacher ground truth for a question.
    /// </summary>
    [HttpGet("question/{questionId:guid}")]
    public async Task<IActionResult> ForQuestion(
        Guid questionId,
        [FromQuery] string? provider,
        [FromQuery] string? modelName,
        CancellationToken ct) =>
        Ok(await _svc.ForQuestionAsync(questionId, provider, modelName, ct));

    /// <summary>Same metrics rolled up across every question of an exam.</summary>
    [HttpGet("exam/{examId:guid}")]
    public async Task<IActionResult> Overall(Guid examId, CancellationToken ct) =>
        Ok(await _svc.OverallAsync(examId, ct));
}
