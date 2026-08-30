using EconGrader.Application.DTOs;
using EconGrader.Application.Exceptions;
using EconGrader.Application.Interfaces;
using EconGrader.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace EconGrader.Application.Services;

/// <summary>Result of an ensemble grading run.</summary>
public sealed record EnsembleResult(
    IReadOnlyList<GradingRun> Runs,
    int ValidRuns,
    decimal? MedianAiScore);

/// <summary>
/// Orchestrates a grading run: builds the request, calls the Python service
/// (via IGradingClient — never a provider SDK directly), persists the run
/// with full audit trail. Teacher score is NEVER included in the request.
/// </summary>
public interface IGradingOrchestrationService
{
    Task<EnsembleResult> GradeAnswerAsync(
        Guid answerId,
        decimal temperature,
        string promptVersion,
        CancellationToken ct = default);

    /// <summary>Trimmed runs (no RawAiResponse) for list/timeline views.</summary>
    Task<IReadOnlyList<GradingRunSummaryDetailDto>> GetRunsForAnswerAsync(Guid answerId, CancellationToken ct = default);
    /// <summary>Full run including RawAiResponse.</summary>
    Task<GradingRun?> GetRunAsync(Guid runId, CancellationToken ct = default);
}

public sealed class GradingOrchestrationService : IGradingOrchestrationService
{
    // Runs are immutable evidence rows: criteria/validation JSON is persisted in
    // the SAME camelCase shape the API contract exposes. Default serialization
    // would emit PascalCase and crash the frontend's criteria table.
    private static readonly JsonSerializerOptions PersistedJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IAppDbContext _db;
    private readonly IGradingClient _gradingClient;
    private readonly IFileStorage _storage;
    private readonly IAuditLogger _audit;
    private readonly ILogger<GradingOrchestrationService> _logger;

    public GradingOrchestrationService(
        IAppDbContext db,
        IGradingClient gradingClient,
        IFileStorage storage,
        IAuditLogger audit,
        ILogger<GradingOrchestrationService> logger)
    {
        _db = db;
        _gradingClient = gradingClient;
        _storage = storage;
        _audit = audit;
        _logger = logger;
    }

    public async Task<EnsembleResult> GradeAnswerAsync(
        Guid answerId, decimal temperature, string promptVersion, CancellationToken ct = default)
    {
        var answer = await _db.Answers
            .Include(a => a.Student)
            .Include(a => a.Question)
            .FirstOrDefaultAsync(a => a.Id == answerId, ct)
            ?? throw new NotFoundException(nameof(Answer), answerId);

        var rubric = await GetActiveRubricAsync(answer.QuestionId, ct);

        // Attach every stored file that exists on disk, keeping roles apart:
        // question-paper files become part of the question statement (merged
        // with the typed question text by the grading service), rubric files
        // are routed as rubric material.
        var questionFiles = new List<string>();
        var rubricFiles = new List<string>();
        void AddIfPresent(string? key, List<string> target)
        {
            if (!string.IsNullOrEmpty(key) && _storage.Exists(key))
                target.Add(_storage.GetAbsolutePath(key));
        }
        AddIfPresent(answer.Question.FileStorageKey, questionFiles);   // question paper
        AddIfPresent(rubric.FileStorageKey, rubricFiles);              // rubric document

        var answerPath = string.IsNullOrEmpty(answer.ImageStorageKey) || !_storage.Exists(answer.ImageStorageKey)
            ? null
            : _storage.GetAbsolutePath(answer.ImageStorageKey);
        if (answerPath is null)
            throw new NotFoundException("Answer image", answerId);

        var request = new GradingServiceRequest(
            StudentId: answer.Student.ExternalId,
            QuestionId: answer.QuestionId.ToString(),
            QuestionText: answer.Question.Text ?? string.Empty,
            Rubric: new GradingRubricDto(rubric.Criteria.Select(c =>
                new GradingCriterionDto(c.CriterionId, c.Description, c.MaxScore)).ToList()),
            AnswerImagePaths: new[] { answerPath },
            // Question paper rides as question material; the grading service
            // merges its extracted text into the question statement per
            // provider capability.
            QuestionImagePaths: questionFiles.ToArray(),
            RubricFilePaths: rubricFiles.ToArray(),
            MaxScore: answer.Question.MaxScore,
            Temperature: temperature,
            PromptVersion: promptVersion
        );

        _logger.LogInformation(
            "Grading request prepared AnswerId={AnswerId} QuestionFiles={QuestionFiles} RubricFiles={RubricFiles}",
            answerId, questionFiles.Count, rubricFiles.Count);

        // CRITICAL: teacher score is NEVER sent — only AI's independent view.
        var response = await _gradingClient.GradeAsync(request, ct);

        var run = new GradingRun
        {
            AnswerId = answer.Id,
            QuestionId = answer.QuestionId,
            StudentId = answer.StudentId,
            Provider = response.Provider,
            ModelName = response.ModelName,
            ModelVersion = response.ModelVersion,
            Temperature = temperature,
            PromptVersion = promptVersion,
            AiScore = response.AiScore,
            // Snapshot for later comparison (never part of the request payload)
            TeacherScoreSnapshot = answer.TeacherScore,
            RawAiResponse = response.RawResponse,
            IsValid = response.IsValid,
            ValidationErrorsJson = JsonSerializer.Serialize(response.ValidationErrors, PersistedJson),
            CriteriaScoresJson = JsonSerializer.Serialize(response.CriteriaScores, PersistedJson),
            Reasoning = response.Reasoning,
            LatencyMs = response.LatencyMs,
            InputTokens = response.InputTokens,
            OutputTokens = response.OutputTokens,
            EstimatedCost = response.EstimatedCostUsd,
            Error = response.Error,
        };

        _db.GradingRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("GradingRunCompleted", "GradingRun", run.Id, null, new
        {
            run.AnswerId,
            run.Provider,
            run.ModelName,
            run.PromptVersion,
            run.AiScore,
            run.IsValid,
            run.Error,
        });

        if (!response.IsValid || response.Error != null)
        {
            _logger.LogWarning("Invalid grading run {RunId} for answer {AnswerId}: {Errors}",
                run.Id, answerId, string.Join("; ", response.ValidationErrors));
        }
        return new EnsembleResult(new[] { run }, run.IsValid ? 1 : 0, run.AiScore);
    }

    public async Task<IReadOnlyList<GradingRunSummaryDetailDto>> GetRunsForAnswerAsync(Guid answerId, CancellationToken ct = default)
    {
        var runs = await _db.GradingRuns
            .Where(r => r.AnswerId == answerId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        // RawAiResponse excluded from listings — fetch GET /api/grading/run/{id} for it.
        return runs.Select(GradingRunSummaryDetailDto.From).ToList();
    }

    public Task<GradingRun?> GetRunAsync(Guid runId, CancellationToken ct = default) =>
        _db.GradingRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);

    private async Task<Rubric> GetActiveRubricAsync(Guid questionId, CancellationToken ct)
    {
        var rubric = await _db.Rubrics
            .Include(r => r.Criteria.OrderBy(c => c.Order))
            .Where(r => r.QuestionId == questionId && r.IsActive)
            .OrderByDescending(r => r.Version)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Active rubric for question", questionId);

        // A rubric with zero criteria AND no rubric document leaves the AI with
        // nothing to grade against — it either invents criteria (validation
        // failure) or returns a 0 with no justification. Surface it as a clear
        // client error. BUT a rubric that ships its criteria as an attached
        // document (Excel/CSV/PDF/DOCX) is legitimate with zero structured
        // criteria rows — the grading service extracts the document and the AI
        // grades against it, so that case must NOT be blocked here.
        if (rubric.Criteria.Count == 0 && string.IsNullOrEmpty(rubric.FileStorageKey))
            throw new BusinessRuleException(
                $"The active rubric for question {questionId} has no criteria and no rubric document — add at least one criterion or upload a rubric file before grading.",
                "RUBRIC_EMPTY_CRITERIA");

        return rubric;
    }
}