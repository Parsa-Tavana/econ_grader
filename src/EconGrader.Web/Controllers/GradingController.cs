using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    private readonly IGradingJobService _jobs;
    private readonly IAppDbContext _db;
    private readonly IGradingClient _gradingClient;
    private readonly IAccessScopeService _scope;
    private readonly CurrentUser _user;
    private readonly GradingJobOptions _jobOptions;
    private readonly bool _queueMode;

    public GradingController(IGradingOrchestrationService orchestrator, IGradingJobService jobs,
        IAppDbContext db, IGradingClient gradingClient, IAccessScopeService scope, CurrentUser user,
        IOptions<GradingJobOptions> jobOptions, IConfiguration config)
    {
        _orchestrator = orchestrator;
        _jobs = jobs;
        _db = db;
        _gradingClient = gradingClient;
        _scope = scope;
        _user = user;
        _jobOptions = jobOptions.Value;
        // Grading:Mode=sync (default) | queue — one release of coexistence,
        // then sync goes away (foundation plan M2).
        _queueMode = string.Equals(config["Grading:Mode"], "queue", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class GradeRequestDto
    {
        public Guid AnswerId { get; set; }
        /// <summary>0.0 for deterministic; up to ~0.4 for ensemble runs.</summary>
        public decimal Temperature { get; set; } = 0m;
        /// <summary>Prompt template version from Python service.</summary>
        public string PromptVersion { get; set; } = "default";
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

        // ── Queue mode (M2): validate + enqueue → 202 with job ids. Ensemble
        // = N jobs; workers drain them under the global in-flight cap.
        if (_queueMode)
        {
            var jobs = await _jobs.EnqueueAsync(
                request.AnswerId, request.Temperature, request.PromptVersion, request.Runs,
                _user.UserId, ct);
            return Accepted(new
            {
                jobs = jobs.Select(GradingJobDto.From),
                statusUrl = $"/api/grading/jobs?answerId={request.AnswerId}",
            });
        }

        // ── Sync mode (legacy default): block until every run finishes.
        // M5: ensemble slot i rides at TemperatureForIndex(i) — with the
        // default stagger of 0 every slot matches the request exactly (no
        // behavior change); a configured stagger separates the samples.
        var allRuns = new List<GradingRun>();
        for (int i = 0; i < request.Runs; i++)
        {
            var result = await _orchestrator.GradeAnswerAsync(
                request.AnswerId, _jobOptions.TemperatureForIndex(request.Temperature, i), request.PromptVersion, ct);
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

    /// <summary>Job status list — M2 queue mode. Filter by answer or exam
    /// (+ optional status). Teachers see their own exams' jobs; students never.</summary>
    [HttpGet("jobs")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<ActionResult<IReadOnlyList<GradingJobDto>>> ListJobs(
        [FromQuery] Guid? answerId, [FromQuery] Guid? examId, [FromQuery] Guid? questionId,
        [FromQuery] string? status, CancellationToken ct)
    {
        var query = _db.GradingJobs.AsNoTracking().AsQueryable();
        if (answerId is { } aid) query = query.Where(j => j.AnswerId == aid);
        if (questionId is { } qid) query = query.Where(j => _db.Answers.Any(a => a.Id == j.AnswerId && a.QuestionId == qid));
        if (examId is { } eid)
        {
            var examAnswers = _db.Answers.Where(a => a.Question.ExamId == eid).Select(a => a.Id);
            query = query.Where(j => examAnswers.Contains(j.AnswerId));
        }
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(j => j.Status == status.Trim().ToLowerInvariant());

        // Scope: teachers/correctors may only see jobs on accessible answers.
        if (!_user.IsAdmin)
        {
            var accessible = await _scope.GetAccessibleExamIdsAsync(_user, ct);
            query = query.Where(j => _db.Answers
                .Any(a => a.Id == j.AnswerId && accessible.Contains(a.Question.ExamId)));
        }

        var jobs = await query
            .OrderByDescending(j => j.CreatedAt)
            .Take(500)
            .ToListAsync(ct);
        return Ok(jobs.Select(GradingJobDto.From).ToList());
    }

    /// <summary>Bulk grading (M3): enqueue one job per answer for the exam
    /// (or one question). "Grade all remaining" — answers that already have a
    /// run are skipped; per-answer failures never block the batch (each job's
    /// failure is recorded on its own row and retried independently).</summary>
    [HttpPost("bulk")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<IActionResult> BulkGrade(
        [FromBody] BulkGradeRequestDto request,
        CancellationToken ct)
    {
        // Exam-level access (write) — bulk never touches one answer directly.
        var accessible = await _scope.GetAccessibleExamIdsAsync(_user, ct);
        if (!_user.IsAdmin && !accessible.Contains(request.ExamId)) return Forbid();

        // Candidate answers: exam scope (or one question), must have a file.
        var answersQuery = _db.Answers
            .Where(a => a.Question.ExamId == request.ExamId && a.ImageStorageKey != null && a.ImageStorageKey != "");
        if (request.QuestionId is { } qid)
            answersQuery = answersQuery.Where(a => a.QuestionId == qid);

        var candidates = await answersQuery
            .OrderBy(a => a.Question.DisplayOrder).ThenBy(a => a.Student.ExternalId)
            .Select(a => new { a.Id, RunCount = a.GradingRuns.Count() })
            .ToListAsync(ct);

        // "Grade all remaining": skip answers that already have a run.
        var targets = candidates.Where(a => a.RunCount == 0).ToList();
        if (targets.Count == 0)
            return Ok(new { enqueued = 0, skipped = candidates.Count, message = "All answers already graded (or nothing to grade)" });

        var user = _user.UserId;
        var perAnswer = new List<object>();
        foreach (var t in targets)
        {
            var jobs = await _jobs.EnqueueAsync(t.Id, request.Temperature, request.PromptVersion, request.Runs, user, ct);
            perAnswer.Add(new { answerId = t.Id, jobs = jobs.Select(GradingJobDto.From) });
        }

        return Accepted(new
        {
            enqueued = targets.Count,
            skipped = candidates.Count - targets.Count,
            answers = perAnswer,
            statusUrl = $"/api/grading/jobs?examId={request.ExamId}",
        });
    }

    public sealed record BulkGradeRequestDto(
        Guid ExamId,
        Guid? QuestionId,
        decimal Temperature = 0m,
        string PromptVersion = "default",
        int Runs = 1);

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
