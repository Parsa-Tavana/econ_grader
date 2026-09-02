using EconGrader.Application.DTOs;
using EconGrader.Application.Exceptions;
using EconGrader.Application.Interfaces;
using EconGrader.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EconGrader.Application.Services;

/// <summary>
/// AI extraction of questions + rubric criteria from ONE exam-wide rubric
/// document (the grading key). Preview saves nothing; Apply upserts the
/// confirmed rows by question number — existing questions not present in the
/// payload are left untouched (answers + grading history preserved).
/// </summary>
public interface IExamExtractionService
{
    /// <summary>Run extraction and return the editable preview. Saves nothing.</summary>
    Task<ExtractionPreviewDto> ExtractPreviewAsync(Guid examId, CancellationToken ct = default);

    /// <summary>Persist confirmed extraction rows: update matching numbers,
    /// create missing ones, never delete others.</summary>
    Task<ApplyExtractionResultDto> ApplyAsync(Guid examId, ApplyExtractionRequest request, Guid userId, CancellationToken ct = default);
}

public sealed class ExamExtractionService : IExamExtractionService
{
    private readonly IAppDbContext _db;
    private readonly IGradingClient _gradingClient;
    private readonly IFileStorage _storage;
    private readonly IAuditLogger _audit;
    private readonly ILogger<ExamExtractionService> _logger;

    public ExamExtractionService(
        IAppDbContext db,
        IGradingClient gradingClient,
        IFileStorage storage,
        IAuditLogger audit,
        ILogger<ExamExtractionService> logger)
    {
        _db = db;
        _gradingClient = gradingClient;
        _storage = storage;
        _audit = audit;
        _logger = logger;
    }

    public async Task<ExtractionPreviewDto> ExtractPreviewAsync(Guid examId, CancellationToken ct = default)
    {
        var exam = await _db.Exams.FirstOrDefaultAsync(e => e.Id == examId, ct)
            ?? throw new NotFoundException(nameof(Exam), examId);

        if (string.IsNullOrEmpty(exam.RubricFileStorageKey) || !_storage.Exists(exam.RubricFileStorageKey))
            throw new BusinessRuleException(
                "Upload the exam rubric file (grading key) first — extraction reads the questions from it.",
                "EXAM_RUBRIC_FILE_MISSING");

        var absolutePath = _storage.GetAbsolutePath(exam.RubricFileStorageKey);
        var response = await _gradingClient.ExtractAsync(absolutePath, exam.RubricFileName ?? "rubric", ct);

        if (response.Error is not null || (!response.IsValid && response.Questions.Count == 0))
            throw new DependencyException("GradingService",
                response.Error is not null
                    ? response.Error
                    : string.Join("; ", response.ValidationErrors));

        return new ExtractionPreviewDto(
            ExamId: exam.Id,
            FileName: exam.RubricFileName,
            ContentType: exam.RubricFileContentType,
            Questions: response.Questions.Select(q => new ExtractedQuestionDto(
                q.Number, q.Text, q.MaxScore,
                q.Criteria.Select(c => new ExtractedCriterionDto(c.Id, c.Description, c.MaxScore)).ToList())).ToList(),
            Warnings: response.Warnings,
            Provider: response.Provider,
            ModelName: response.ModelName,
            InputTokens: response.InputTokens,
            OutputTokens: response.OutputTokens,
            LatencyMs: response.LatencyMs,
            EstimatedCostUsd: response.EstimatedCostUsd);
    }

    public async Task<ApplyExtractionResultDto> ApplyAsync(
        Guid examId, ApplyExtractionRequest request, Guid userId, CancellationToken ct = default)
    {
        var exam = await _db.Exams.FirstOrDefaultAsync(e => e.Id == examId, ct)
            ?? throw new NotFoundException(nameof(Exam), examId);

        ValidateApplyRequest(request);

        // One query for everything the upsert needs: existing questions with
        // their rubric versions + criteria, criteria ordered as saved.
        var existing = await _db.Questions
            .Where(q => q.ExamId == examId)
            .Include(q => q.Rubrics.OrderBy(r => r.Version))
                .ThenInclude(r => r.Criteria.OrderBy(c => c.Order))
            .ToDictionaryAsync(q => q.Number, q => q, ct);

        int created = 0, updated = 0, rubricsCreated = 0, untouched = 0;
        var newQuestions = new List<Question>();
        var newRubrics = new List<Rubric>();

        foreach (var row in request.Questions)
        {
            var text = row.Text.Trim();
            if (existing.TryGetValue(row.Number, out var question))
            {
                var textChanged = question.Text != text;
                var scoreChanged = question.MaxScore != row.MaxScore;
                if (textChanged) question.Text = text;
                if (scoreChanged) question.MaxScore = row.MaxScore;

                var active = question.Rubrics
                    .Where(r => r.IsActive)
                    .OrderByDescending(r => r.Version)
                    .FirstOrDefault();
                if (active is not null && CriteriaMatch(active, row.Criteria))
                {
                    // identical criteria — no version churn on re-extraction.
                    // updated/untouched stay disjoint: only content changes count.
                    if (textChanged || scoreChanged) updated++; else untouched++;
                    continue;
                }
                updated++;

                var maxVersion = question.Rubrics.Count == 0 ? 0 : question.Rubrics.Max(r => r.Version);
                var rubric = new Rubric
                {
                    QuestionId = question.Id,
                    Version = maxVersion + 1,
                    IsActive = true,
                    CreatedByUserId = userId,
                };
                foreach (var c in row.Criteria)
                    rubric.Criteria.Add(new RubricCriterion
                    {
                        CriterionId = c.CriterionId.Trim(),
                        Description = c.Description.Trim(),
                        MaxScore = c.MaxScore,
                        Order = rubric.Criteria.Count,
                    });
                // Explicit Add is REQUIRED here: question is tracked Unchanged and
                // the Rubric has a client-set GUID, so navigation-only attach
                // (question.Rubrics.Add) makes DetectChanges mark the rubric AND
                // its criteria Unchanged — EF then issues UPDATEs that hit 0 rows.
                _db.Rubrics.Add(rubric);
                question.Rubrics.Add(rubric);
                if (active is not null) active.IsActive = false;
                rubricsCreated++;
                newRubrics.Add(rubric);
            }
            else
            {
                question = new Question
                {
                    ExamId = exam.Id,
                    Number = row.Number,
                    Text = text,
                    MaxScore = row.MaxScore,
                    // DisplayOrder = Number so an extraction displays Q1..Qn in
                    // numeric order even alongside manually-added questions.
                    DisplayOrder = row.Number,
                };
                var rubric = new Rubric
                {
                    QuestionId = question.Id,
                    Version = 1,
                    IsActive = true,
                    CreatedByUserId = userId,
                };
                foreach (var c in row.Criteria)
                    rubric.Criteria.Add(new RubricCriterion
                    {
                        CriterionId = c.CriterionId.Trim(),
                        Description = c.Description.Trim(),
                        MaxScore = c.MaxScore,
                        Order = rubric.Criteria.Count,
                    });
                question.Rubrics.Add(rubric);
                _db.Questions.Add(question);
                existing[row.Number] = question;
                created++;
                rubricsCreated++;
                newQuestions.Add(question);
                newRubrics.Add(rubric);
            }
        }

        try
        {
            // Single save = implicit transaction; the unique indexes
            // (ExamId,Number) and (QuestionId,Version) catch concurrent applies.
            // Audits are collected in newQuestions/newRubrics and written AFTER
            // this save — AuditLogger.WriteAsync saves internally, so writing
            // them mid-loop would flush the transaction before the
            // conflict-checked save and break all-or-nothing apply.
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Log the cause — a bare "conflict" message hides whether it was a
            // genuine concurrent-apply collision or a constraint we violated.
            var entries = ex.Entries.Count == 0
                ? "no entries"
                : string.Join(", ", ex.Entries.Select(e => e.Entity.GetType().Name));
            _logger.LogError(ex, "Extraction apply SaveChanges failed for exam {ExamId} (entries: {Entries}): {Message}",
                examId, entries, ex.InnerException?.Message ?? ex.Message);
            throw new BusinessRuleException(
                "Exam questions were modified concurrently — reload the exam and retry.",
                "EXTRACTION_CONFLICT");
        }

        // Post-save audits (each its own save; the apply itself is already durable).
        foreach (var q in newQuestions)
            await _audit.WriteAsync("QuestionCreated", "Question", q.Id, userId, new { q.ExamId, q.Number });
        foreach (var r in newRubrics)
            await _audit.WriteAsync("RubricCreated", "Rubric", r.Id, userId, new { r.QuestionId, r.Version });
        await _audit.WriteAsync("ExtractionApplied", "Exam", examId, userId, new
        {
            examId,
            created,
            updated,
            rubricsCreated,
            untouched,
        });

        return new ApplyExtractionResultDto(created, updated, rubricsCreated, untouched);
    }

    private static bool CriteriaMatch(Rubric active, IReadOnlyList<ApplyExtractionCriterionDto> incoming)
    {
        var ordered = active.Criteria.OrderBy(c => c.Order).ToList();
        if (ordered.Count != incoming.Count) return false;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!string.Equals(ordered[i].CriterionId, incoming[i].CriterionId.Trim(), StringComparison.Ordinal))
                return false;
            if (!string.Equals(ordered[i].Description.Trim(), incoming[i].Description.Trim(), StringComparison.Ordinal))
                return false;
            if (ordered[i].MaxScore != incoming[i].MaxScore)
                return false;
        }
        return true;
    }

    private static void ValidateApplyRequest(ApplyExtractionRequest request)
    {
        if (request.Questions is null || request.Questions.Count == 0)
            throw new BusinessRuleException("No confirmed questions to apply.", "EMPTY_EXTRACTION");

        var seen = new HashSet<int>();
        foreach (var q in request.Questions)
        {
            if (q.Number <= 0)
                throw new BusinessRuleException($"Question number must be positive (got {q.Number}).", "INVALID_QUESTION_NUMBER");
            if (!seen.Add(q.Number))
                throw new BusinessRuleException($"Question {q.Number} appears more than once — numbers must be unique.", "DUPLICATE_QUESTION_NUMBER");
            if (string.IsNullOrWhiteSpace(q.Text))
                throw new BusinessRuleException($"Question {q.Number}: text is required.", "INVALID_QUESTION_TEXT");
            if (q.MaxScore <= 0)
                throw new BusinessRuleException($"Question {q.Number}: max score must be positive.", "INVALID_QUESTION_MAX_SCORE");
            if (q.Criteria is null || q.Criteria.Count == 0)
                throw new BusinessRuleException($"Question {q.Number}: at least one criterion is required.", "EMPTY_CRITERIA");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            decimal sum = 0;
            foreach (var c in q.Criteria)
            {
                if (string.IsNullOrWhiteSpace(c.CriterionId))
                    throw new BusinessRuleException($"Question {q.Number}: every criterion needs an id.", "INVALID_CRITERION_ID");
                if (!ids.Add(c.CriterionId.Trim()))
                    throw new BusinessRuleException($"Question {q.Number}: duplicate criterion id '{c.CriterionId.Trim()}'.", "DUPLICATE_CRITERION_ID");
                if (string.IsNullOrWhiteSpace(c.Description))
                    throw new BusinessRuleException($"Question {q.Number} criterion '{c.CriterionId.Trim()}': description is required.", "INVALID_CRITERION_DESCRIPTION");
                if (c.MaxScore <= 0)
                    throw new BusinessRuleException($"Question {q.Number} criterion '{c.CriterionId.Trim()}': max score must be positive.", "INVALID_CRITERION_MAX_SCORE");
                sum += c.MaxScore;
            }
            if (sum > q.MaxScore + 0.0000000001m)
                throw new BusinessRuleException(
                    $"Question {q.Number}: criteria sum ({sum:0.##}) exceeds the question's max score ({q.MaxScore:0.##}).",
                    "CRITERIA_SUM_EXCEEDS_MAX");
        }
    }
}
