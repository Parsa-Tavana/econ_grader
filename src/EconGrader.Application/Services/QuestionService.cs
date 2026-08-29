using EconGrader.Application.DTOs;
using EconGrader.Application.Exceptions;
using EconGrader.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace EconGrader.Application.Services;

public interface IQuestionService
{
    Task<QuestionDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<QuestionDto>> ListByExamAsync(Guid examId, CancellationToken ct = default);
    Task<QuestionDto> CreateAsync(CreateQuestionRequest request, CancellationToken ct = default);
    Task<QuestionDto?> UpdateAsync(Guid id, string? text, decimal? maxScore, string? rubricText, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<RubricDto?> GetActiveRubricAsync(Guid questionId, CancellationToken ct = default);
    Task<RubricDto> CreateRubricAsync(CreateRubricRequest request, Guid createdByUserId, CancellationToken ct = default);
}

public sealed class QuestionService : IQuestionService
{
    private readonly IAppDbContext _db;
    private readonly IAuditLogger _audit;

    public QuestionService(IAppDbContext db, IAuditLogger audit) { _db = db; _audit = audit; }

    public async Task<QuestionDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var q = await _db.Questions.FindAsync([id], ct);
        return q is null ? null : Map(q);
    }

    public async Task<IReadOnlyList<QuestionDto>> ListByExamAsync(Guid examId, CancellationToken ct = default)
    {
        var questions = await _db.Questions
            .Where(q => q.ExamId == examId)
            .OrderBy(q => q.DisplayOrder).ThenBy(q => q.Number)
            .ToListAsync(ct);
        return questions.Select(Map).ToList();
    }

    public async Task<QuestionDto> CreateAsync(CreateQuestionRequest request, CancellationToken ct = default)
    {
        // BUG-001: pre-check duplicate question number before hitting the DB unique index.
        // Without this, a DbUpdateException bubbles up as a generic 500.
        bool duplicate = await _db.Questions
            .AnyAsync(q => q.ExamId == request.ExamId && q.Number == request.Number, ct);
        if (duplicate)
            throw new BusinessRuleException(
                $"A question with number {request.Number} already exists in this exam",
                "DUPLICATE_QUESTION_NUMBER", 409);

        var maxOrder = await _db.Questions
            .Where(q => q.ExamId == request.ExamId)
            .MaxAsync(q => (int?)q.DisplayOrder, ct) ?? 0;

        var q = new Question
        {
            ExamId = request.ExamId,
            Number = request.Number,
            Text = request.Text,
            MaxScore = request.MaxScore,
            RubricText = request.RubricText,
            DisplayOrder = maxOrder + 1,
        };
        _db.Questions.Add(q);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("QuestionCreated", "Question", q.Id, null, new { q.ExamId, q.Number });
        return Map(q);
    }

    public async Task<QuestionDto?> UpdateAsync(Guid id, string? text, decimal? maxScore, string? rubricText, CancellationToken ct = default)
    {
        var q = await _db.Questions.FindAsync([id], ct);
        if (q is null) return null;
        if (text != null) q.Text = text;
        if (maxScore.HasValue) q.MaxScore = maxScore.Value;
        if (rubricText != null) q.RubricText = rubricText;
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("QuestionUpdated", "Question", q.Id, null, new { q.MaxScore });
        return Map(q);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var q = await _db.Questions.FindAsync([id], ct);
        if (q is null) return false;
        _db.Questions.Remove(q);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("QuestionDeleted", "Question", q.Id, null);
        return true;
    }

    public async Task<RubricDto?> GetActiveRubricAsync(Guid questionId, CancellationToken ct = default)
    {
        var r = await _db.Rubrics
            .Include(x => x.Criteria)
            .Where(x => x.QuestionId == questionId && x.IsActive)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(ct);
        return r is null ? null : MapRubric(r);
    }

    public async Task<RubricDto> CreateRubricAsync(CreateRubricRequest request, Guid createdByUserId, CancellationToken ct = default)
    {
        // BUG-002: reject empty criteria — creates a "poison" rubric with totalMaxScore=0.
        if (request.Criteria is null || request.Criteria.Count == 0)
            throw new BusinessRuleException(
                "At least one criterion is required",
                "EMPTY_CRITERIA");

        var maxVersion = await _db.Rubrics
            .Where(r => r.QuestionId == request.QuestionId)
            .MaxAsync(r => (int?)r.Version, ct) ?? 0;

        var rubric = new Rubric
        {
            QuestionId = request.QuestionId,
            Version = maxVersion + 1,
            IsActive = true,
            CreatedByUserId = createdByUserId,
        };
        foreach (var c in request.Criteria)
        {
            rubric.Criteria.Add(new RubricCriterion
            {
                CriterionId = c.CriterionId,
                Description = c.Description,
                MaxScore = c.MaxScore,
                Order = rubric.Criteria.Count,
            });
        }
        _db.Rubrics.Add(rubric);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("RubricCreated", "Rubric", rubric.Id, createdByUserId, new { rubric.QuestionId, rubric.Version });
        return MapRubric(rubric);
    }

    private static QuestionDto Map(Question q) =>
        new(q.Id, q.ExamId, q.Number, q.Text, q.MaxScore, q.RubricText,
            FileName: q.FileName, ContentType: q.ContentType);

    private static RubricDto MapRubric(Rubric r) => new(
        r.Id, r.QuestionId, r.Version, r.IsActive, r.CreatedAt,
        r.Criteria.Sum(c => c.MaxScore),
        r.Criteria.OrderBy(c => c.Order).Select(c =>
            new RubricCriterionDto(c.CriterionId, c.Description, c.MaxScore, c.Order)).ToList(),
        FileName: r.FileName, ContentType: r.ContentType);
}