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
        string? provider = null,
        CancellationToken ct = default);

    /// <summary>Trimmed runs (no RawAiResponse) for list/timeline views.</summary>
    Task<IReadOnlyList<GradingRunSummaryDetailDto>> GetRunsForAnswerAsync(Guid answerId, CancellationToken ct = default);
    /// <summary>Full run including RawAiResponse.</summary>
    Task<GradingRun?> GetRunAsync(Guid runId, CancellationToken ct = default);
}

public sealed class GradingOrchestrationService : IGradingOrchestrationService
{
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
        Guid answerId, decimal temperature, string promptVersion, string? provider = null, CancellationToken ct = default)
    {
        var answer = await _db.Answers
            .Include(a => a.Student)
            .Include(a => a.Question)
            .FirstOrDefaultAsync(a => a.Id == answerId, ct)
            ?? throw new NotFoundException(nameof(Answer), answerId);

        var rubric = await GetActiveRubricAsync(answer.QuestionId, ct);

        // Attach every stored file that exists on disk. The Python service /
        // provider layer decides how to represent each (image vs rendered
        // pages vs extracted text) based on its own capabilities.
        var filePaths = new List<string>();
        void AddIfPresent(string? key)
        {
            if (!string.IsNullOrEmpty(key) && _storage.Exists(key))
                filePaths.Add(_storage.GetAbsolutePath(key));
        }
        AddIfPresent(answer.Question.FileStorageKey);   // question paper
        AddIfPresent(rubric.FileStorageKey);            // rubric document
        AddIfPresent(answer.ImageStorageKey);           // student answer

        var request = new GradingServiceRequest(
            StudentId: answer.Student.ExternalId,
            QuestionId: answer.QuestionId.ToString(),
            QuestionText: answer.Question.Text,
            Rubric: new GradingRubricDto(rubric.Criteria.Select(c =>
                new GradingCriterionDto(c.CriterionId, c.Description, c.MaxScore)).ToList()),
            AnswerImagePaths: new[] { _storage.GetAbsolutePath(answer.ImageStorageKey) },
            // All supporting documents (question paper + rubric doc) ride along;
            // the grading service routes them to the AI per provider capability.
            QuestionImagePaths: filePaths.Where(p => p != _storage.GetAbsolutePath(answer.ImageStorageKey)).ToArray(),
            MaxScore: answer.Question.MaxScore,
            Temperature: temperature,
            PromptVersion: promptVersion,
            Provider: provider
        );

        _logger.LogInformation(
            "Grading request prepared AnswerId={AnswerId} Files={FileCount} Provider={Provider}",
            answerId, filePaths.Count, provider ?? "default");

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
            ValidationErrorsJson = JsonSerializer.Serialize(response.ValidationErrors),
            CriteriaScoresJson = JsonSerializer.Serialize(response.CriteriaScores),
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
        return await _db.Rubrics
            .Include(r => r.Criteria.OrderBy(c => c.Order))
            .Where(r => r.QuestionId == questionId && r.IsActive)
            .OrderByDescending(r => r.Version)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Active rubric for question", questionId);
    }
}