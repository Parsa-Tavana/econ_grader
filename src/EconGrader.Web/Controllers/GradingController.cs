using Microsoft.AspNetCore.Mvc;
using EconGrader.Application.DTOs;
using EconGrader.Application.Interfaces;
using EconGrader.Application.Services;
using EconGrader.Domain.Entities;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class GradingController : ControllerBase
{
    private readonly IGradingOrchestrationService _orchestrator;
    private readonly IGradingClient _gradingClient;

    public GradingController(IGradingOrchestrationService orchestrator, IGradingClient gradingClient)
    {
        _orchestrator = orchestrator;
        _gradingClient = gradingClient;
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
    /// </summary>
    [HttpPost("run")]
    public async Task<ActionResult<GradeResultDto>> Grade(
        [FromBody] GradeRequestDto request,
        CancellationToken ct)
    {
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

    /// <summary>All grading runs recorded for one answer (trimmed — no RawAiResponse).</summary>
    [HttpGet("answer/{answerId:guid}")]
    public async Task<ActionResult<IReadOnlyList<GradingRunSummaryDetailDto>>> ListForAnswer(Guid answerId, CancellationToken ct) =>
        Ok(await _orchestrator.GetRunsForAnswerAsync(answerId, ct));

    /// <summary>Full detail of a single run including raw AI response and per-criterion scores.</summary>
    [HttpGet("run/{runId:guid}")]
    public async Task<ActionResult<GradingRun>> GetRun(Guid runId, CancellationToken ct) =>
        await _orchestrator.GetRunAsync(runId, ct) is { } run ? Ok(run) : NotFound();

    /// <summary>Available prompt versions from the Python grading service.</summary>
    [HttpGet("prompts")]
    public async Task<IActionResult> GetPromptVersions(CancellationToken ct) =>
        Ok(new { prompts = await _gradingClient.GetPromptVersionsAsync(ct) });
}