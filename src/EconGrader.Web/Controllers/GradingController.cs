using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EconGrader.Application.DTOs;
using EconGrader.Application.Interfaces;
using EconGrader.Application.Services;
using EconGrader.Domain.Entities;
using EconGrader.Web.Services;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class GradingController : ControllerBase
{
    private readonly IGradingOrchestrationService _orchestrator;
    private readonly IGradingClient _gradingClient;
    private readonly IAccessScopeService _scope;
    private readonly CurrentUser _user;

    public GradingController(IGradingOrchestrationService orchestrator, IGradingClient gradingClient,
        IAccessScopeService scope, CurrentUser user)
    {
        _orchestrator = orchestrator;
        _gradingClient = gradingClient;
        _scope = scope;
        _user = user;
    }

    public sealed class GradeRequestDto
    {
        public Guid AnswerId { get; set; }
        /// <summary>0.0 for deterministic; up to ~0.4 for ensemble runs.</summary>
        public decimal Temperature { get; set; } = 0m;
        /// <summary>Prompt template version from Python service.</summary>
        public string PromptVersion { get; set; } = "default";
        /// <summary>Optional provider override: "claude" | "gemini" | "qwen".</summary>
        public string? Provider { get; set; }
        /// <summary>Run this many times and keep every result (ensemble).</summary>
        public int Runs { get; set; } = 1;
    }

    public sealed record GradeResultDto(
        IReadOnlyList<GradingRunSummaryDetailDto> Runs,
        int TotalRuns,
        int ValidRuns,
        decimal? MedianAiScore);

    /// <summary>
    /// Kick off an AI grading run against one answer. The teacher's score is
    /// snapshotted AFTER the AI grades — never included in the request.
    /// Teachers (and admins) only — correctors never trigger AI runs so their
    /// independent review stays uninfluenced by fresh model output.
    /// </summary>
    [HttpPost("run")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<ActionResult<GradeResultDto>> Grade(
        [FromBody] GradeRequestDto request,
        CancellationToken ct)
    {
        if (!await _scope.CanAccessAnswerAsync(_user, request.AnswerId, writeAccess: true, ct)) return Forbid();
        if (request.Runs < 1 || request.Runs > 10)
            return BadRequest(new { code = "INVALID_RUN_COUNT", message = "Runs must be between 1 and 10" });

        var allRuns = new List<GradingRun>();
        for (int i = 0; i < request.Runs; i++)
        {
            var result = await _orchestrator.GradeAnswerAsync(
                request.AnswerId, request.Temperature, request.PromptVersion, request.Provider, ct);
            allRuns.AddRange(result.Runs);
        }

        // Trimmed views — RawAiResponse only via GET /api/grading/run/{id}.
        var runViews = allRuns.Select(GradingRunSummaryDetailDto.From).ToList();

        var validScores = allRuns.Where(r => r.IsValid).Select(r => r.AiScore).OrderBy(s => s).ToList();
        decimal? median = validScores.Count == 0 ? null :
            validScores.Count % 2 == 1
                ? validScores[validScores.Count / 2]
                : (validScores[validScores.Count / 2 - 1] + validScores[validScores.Count / 2]) / 2m;

        return Ok(new GradeResultDto(runViews, allRuns.Count, validScores.Count, median));
    }

    /// <summary>All runs for an answer (trimmed). Students: own answers only,
    /// with teacher-score snapshots hidden.</summary>
    [HttpGet("answer/{answerId:guid}")]
    public async Task<ActionResult<IReadOnlyList<GradingRunSummaryDetailDto>>> ListForAnswer(Guid answerId, CancellationToken ct)
    {
        if (!await _scope.CanAccessAnswerAsync(_user, answerId, writeAccess: false, ct)) return Forbid();
        var runs = await _orchestrator.GetRunsForAnswerAsync(answerId, ct);
        return Ok(_user.IsStudent ? runs.Select(HideTeacherSnapshot).ToList() : runs);
    }

    /// <summary>Full run detail incl. raw AI response and per-criterion scores.
    /// Students get a filtered projection — no raw response, tokens or cost.</summary>
    [HttpGet("run/{runId:guid}")]
    public async Task<ActionResult<GradingRun>> GetRun(Guid runId, CancellationToken ct)
    {
        if (!await _scope.CanAccessRunAsync(_user, runId, ct)) return Forbid();
        var run = await _orchestrator.GetRunAsync(runId, ct);
        if (run is null) return NotFound();

        if (_user.IsStudent)
            return Ok(new
            {
                run.Id,
                run.AnswerId,
                run.QuestionId,
                run.StudentId,
                run.Provider,
                run.ModelName,
                run.PromptVersion,
                run.Temperature,
                run.AiScore,
                TeacherScoreSnapshot = (decimal?)null,
                CriteriaScoresJson = run.CriteriaScoresJson,
                run.Reasoning,
                run.IsValid,
                run.CreatedAt,
                // Deliberately omitted: RawAiResponse, InputTokens, OutputTokens,
                // EstimatedCost, LatencyMs, ValidationErrorsJson, Error.
            });

        return Ok(run);
    }

    /// <summary>Prompt versions — metadata needed to launch runs.</summary>
    [HttpGet("prompts")]
    public async Task<IActionResult> GetPromptVersions(CancellationToken ct) =>
        Ok(new { prompts = await _gradingClient.GetPromptVersionsAsync(ct) });

    private static GradingRunSummaryDetailDto HideTeacherSnapshot(GradingRunSummaryDetailDto r) => r with
    {
        TeacherScoreSnapshot = null
    };
}
