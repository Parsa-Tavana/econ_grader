using EconGrader.Application.DTOs;
using EconGrader.Application.Exceptions;
using EconGrader.Application.Interfaces;
using EconGrader.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    private readonly IArtifactStore _artifacts;
    private readonly IAuditLogger _audit;
    private readonly IOptions<IngestOptions> _ingestOptions;
    private readonly ILogger<GradingOrchestrationService> _logger;

    /// <summary>Rendered format grading prefers (Ingest:PageFormat flag).</summary>
    private string PreferredFormat => _ingestOptions.Value.PageFormat;

    public GradingOrchestrationService(
        IAppDbContext db,
        IGradingClient gradingClient,
        IFileStorage storage,
        IArtifactStore artifacts,
        IAuditLogger audit,
        IOptions<IngestOptions> ingestOptions,
        ILogger<GradingOrchestrationService> logger)
    {
        _db = db;
        _gradingClient = gradingClient;
        _storage = storage;
        _artifacts = artifacts;
        _audit = audit;
        _ingestOptions = ingestOptions;
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

        // ── Resolve grading inputs (M1: artifacts first, legacy fallback) ────
        // Lineage entries record exactly which artifacts this run consumed —
        // [{kind, role, sha256, page}] for pre-rendered pages, [{kind:"legacy_file",
        // role, storage_key}] for the whole-file fallback.
        var lineage = new List<object>();

        // Answer pages: pre-rendered page artifacts when ingest ran; otherwise
        // the legacy whole-file path (Python converts per request as before).
        var answerPagePaths = await ResolveAnswerPagePathsAsync(answer, ct);
        foreach (var p in answerPagePaths)
            lineage.Add(new { kind = "page", role = "answer", sha256 = p.Sha256, page = p.PageNumber });
        if (answerPagePaths.Count == 0 && !string.IsNullOrEmpty(answer.ImageStorageKey))
            lineage.Add(new { kind = "legacy_file", role = "answer", sha256 = (string?)null, storage_key = answer.ImageStorageKey });

        // Question paper: prefer the question's banked page assets
        // (QuestionAsset rows, saved at extraction-apply / upload time);
        // fall back to the whole original file — which, when it was ingested,
        // IS its own set of rendered pages joined in document order.
        var questionPagePaths = await ResolveQuestionPagePathsAsync(answer.Question, ct);
        foreach (var p in questionPagePaths)
            lineage.Add(new { kind = "page", role = "question", sha256 = p.Sha256, page = p.PageNumber });
        if (questionPagePaths.Count == 0 && !string.IsNullOrEmpty(answer.Question.FileStorageKey) && _storage.Exists(answer.Question.FileStorageKey))
            lineage.Add(new { kind = "legacy_file", role = "question", sha256 = (string?)null, storage_key = answer.Question.FileStorageKey });

        // پاسخنامه (model answer) pages — ONLY when the exam gate is on.
        // Gate is default-off, so uploading a پاسخنامه never changes grading
        // until the M4 golden-set evidence unlocks the flag per exam.
        var answerKeyPagePaths = await ResolveAnswerKeyPagePathsAsync(answer.Question, ct);
        var usedAnswerKey = answerKeyPagePaths.Count > 0;
        foreach (var p in answerKeyPagePaths)
            lineage.Add(new { kind = "page", role = "model_answer", sha256 = p.Sha256, page = p.PageNumber });

        string[] answerPaths;
        string[] questionPaths;
        if (answerPagePaths.Count > 0)
        {
            // Pre-rendered path: pages are already JPEG/PNG images on disk —
            // zero conversion in the Python service for the answer side.
            answerPaths = answerPagePaths.Select(p => _artifacts.AbsolutePath(p.StorageKey)).ToArray();
        }
        else
        {
            answerPaths = new[] { _storage.GetAbsolutePath(answer.ImageStorageKey) };
        }

        if (questionPagePaths.Count > 0)
        {
            questionPaths = questionPagePaths.Select(p => _artifacts.AbsolutePath(p.StorageKey)).ToArray();
        }
        else
        {
            questionPaths = Array.Empty<string>();
            if (!string.IsNullOrEmpty(answer.Question.FileStorageKey) && _storage.Exists(answer.Question.FileStorageKey))
                questionPaths = new[] { _storage.GetAbsolutePath(answer.Question.FileStorageKey) };
        }

        if (usedAnswerKey)
        {
            questionPaths = questionPaths
                .Concat(answerKeyPagePaths.Select(p => _artifacts.AbsolutePath(p.StorageKey)))
                .ToArray();
        }

        var request = new GradingServiceRequest(
            StudentId: answer.Student.ExternalId,
            QuestionId: answer.QuestionId.ToString(),
            QuestionText: answer.Question.Text ?? string.Empty,
            Rubric: new GradingRubricDto(rubric.Criteria.Select(c =>
                new GradingCriterionDto(c.CriterionId, c.Description, c.MaxScore)).ToList()),
            AnswerImagePaths: answerPaths,
            // Question paper rides as question material; the grading service
            // merges its extracted text into the question statement per
            // provider capability. Pre-rendered pages arrive as images, so
            // when they came from ingest the file-path images are pages, and
            // the PDF never re-renders here.
            QuestionImagePaths: questionPaths,
            MaxScore: answer.Question.MaxScore,
            Temperature: temperature,
            PromptVersion: promptVersion
        );

        _logger.LogInformation(
            "Grading request prepared AnswerId={AnswerId} QuestionPages={QuestionPages} AnswerPages={AnswerPages} PreRendered={PreRendered} AnswerKeyPages={AnswerKeyPages} RubricCriteria={RubricCriteria}",
            answerId, questionPaths.Length, answerPaths.Length, answerPagePaths.Count > 0, answerKeyPagePaths.Count, rubric.Criteria.Count);

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
            // M4 lineage (written from M1 on): [{kind, role, sha256, page}] —
            // role=model_answer proves a پاسخنامه-graded run consumed it.
            InputArtifactsJson = JsonSerializer.Serialize(lineage, PersistedJson),
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

    /// <summary>Resolve the answer's pre-rendered page artifacts, preferring
    /// the configured grading format. Falls back to an empty list (legacy
    /// whole-file path) when ingest hasn't run for this answer.</summary>
    private async Task<List<Artifact>> ResolveAnswerPagePathsAsync(Answer answer, CancellationToken ct)
    {
        var pages = await _db.AnswerPages
            .Where(p => p.AnswerId == answer.Id)
            .Include(p => p.Artifact)
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);
        if (pages.Count == 0) return [];

        // Prefer the configured grading format (Ingest:PageFormat — png200 is
        // the legacy-safe default; M4's golden-set A/B may flip it to jpeg150).
        var preferred = pages.Where(p => p.Format == PreferredFormat).ToList();
        return preferred.Count > 0 ? preferred.Select(p => p.Artifact).ToList() : pages.Select(p => p.Artifact).ToList();
    }

    /// <summary>Resolve the question paper pages: banked QuestionAsset rows
    /// first (item banking), else the question file's own ingested pages in
    /// document order, else empty (caller falls back to the whole file).</summary>
    private async Task<List<Artifact>> ResolveQuestionPagePathsAsync(Question question, CancellationToken ct)
    {
        var assets = await _db.QuestionAssets
            .Where(a => a.QuestionId == question.Id)
            .Include(a => a.Artifact)
            .OrderBy(a => a.SortOrder)
            .ToListAsync(ct);
        if (assets.Count > 0) return assets.Select(a => a.Artifact).ToList();

        // Whole-file ingest: the question file's rendered pages ARE the item's
        // pages. Only direct children count — deduped pages whose row was first
        // registered under another document would misorder here, and the
        // whole-file fallback below keeps those documents working.
        if (question.FileArtifactId is { } fileArtifactId)
        {
            var pages = await _db.Artifacts
                .Where(a => a.Kind == "page" && a.Format == PreferredFormat && a.ParentArtifactId == fileArtifactId)
                .OrderBy(a => a.PageNumber ?? 0)
                .ToListAsync(ct);
            if (pages.Count > 0) return pages;
        }
        return [];
    }

    /// <summary>Resolve پاسخنامه pages — ONLY when Exam.GroundTruthGradingEnabled
    /// is on (inert by default; M4 flips it per exam with golden-set evidence).</summary>
    private async Task<List<Artifact>> ResolveAnswerKeyPagePathsAsync(Question question, CancellationToken ct)
    {
        // Load the gate without loading the whole exam graph.
        var gateOn = await _db.Exams
            .Where(e => e.Id == question.ExamId)
            .Select(e => e.GroundTruthGradingEnabled)
            .FirstOrDefaultAsync(ct);
        if (!gateOn) return [];

        var keys = await _db.QuestionAnswerKeys
            .Where(k => k.QuestionId == question.Id)
            .Include(k => k.Artifact)
            .OrderBy(k => k.SortOrder)
            .ToListAsync(ct);
        if (keys.Count > 0) return keys.Select(k => k.Artifact).ToList();

        // Per-question file ingest: its rendered pages are the key's pages.
        if (question.AnswerKeyArtifactId is { } keyArtifactId)
        {
            var pages = await _db.Artifacts
                .Where(a => a.Kind == "page" && a.Format == PreferredFormat && a.ParentArtifactId == keyArtifactId)
                .OrderBy(a => a.PageNumber ?? 0)
                .ToListAsync(ct);
            return pages;
        }
        return [];
    }

    private async Task<Rubric> GetActiveRubricAsync(Guid questionId, CancellationToken ct)
    {
        var rubric = await _db.Rubrics
            .Include(r => r.Criteria.OrderBy(c => c.Order))
            .Where(r => r.QuestionId == questionId && r.IsActive)
            .OrderByDescending(r => r.Version)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Active rubric for question", questionId);

        // A rubric with zero criteria leaves the AI with nothing to grade
        // against — it either invents criteria (validation failure) or returns
        // a 0 with no justification. Surface it as a clear client error.
        if (rubric.Criteria.Count == 0)
            throw new BusinessRuleException(
                $"The active rubric for question {questionId} has no criteria — add criteria (or extract them from the exam rubric file) before grading.",
                "RUBRIC_EMPTY_CRITERIA");

        return rubric;
    }
}