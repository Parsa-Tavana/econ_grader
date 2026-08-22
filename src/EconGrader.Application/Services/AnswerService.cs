using EconGrader.Application.DTOs;
using EconGrader.Application.Exceptions;
using EconGrader.Domain.Entities;

namespace EconGrader.Application.Services;

public interface IAnswerService
{
    Task<AnswerDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<AnswerDto> CreateAsync(CreateAnswerRequest request, CancellationToken ct = default);
    Task<AnswerDto> SetTeacherScoreAsync(Guid answerId, decimal score, decimal? teacher2Score = null, CancellationToken ct = default);
    Task<IReadOnlyList<AnswerDto>> ListByQuestionAsync(Guid questionId, CancellationToken ct = default);
}

public record CreateAnswerRequest(
    Guid StudentId,
    Guid QuestionId,
    string ImageStorageKey,
    decimal? TeacherScore = null,
    decimal? Teacher2Score = null
);

public sealed class AnswerService : IAnswerService
{
    private readonly IAppDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IAuditLogger _audit;

    public AnswerService(IAppDbContext db, IFileStorage storage, IAuditLogger audit)
    {
        _db = db;
        _storage = storage;
        _audit = audit;
    }

    public async Task<AnswerDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var a = await _db.Answers
            .Include(x => x.Student)
            .Include(x => x.GradingRuns)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        return a is null ? null : Map(a);
    }

    public async Task<AnswerDto> CreateAsync(CreateAnswerRequest request, CancellationToken ct = default)
    {
        var a = new Answer
        {
            StudentId = request.StudentId,
            QuestionId = request.QuestionId,
            ImageStorageKey = request.ImageStorageKey,
            TeacherScore = request.TeacherScore,
            Teacher2Score = request.Teacher2Score,
        };
        _db.Answers.Add(a);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("AnswerUploaded", "Answer", a.Id, null, new { a.ImageStorageKey, a.QuestionId });
        return await GetAsync(a.Id, ct) ?? throw new NotFoundException(nameof(Answer), a.Id);
    }

    public async Task<AnswerDto> SetTeacherScoreAsync(Guid answerId, decimal score, decimal? teacher2Score, CancellationToken ct = default)
    {
        var a = await _db.Answers.FindAsync([answerId], ct) ?? throw new NotFoundException(nameof(Answer), answerId);
        if (score < 0)
            throw new BusinessRuleException("Teacher score cannot be negative", "INVALID_SCORE");
        a.TeacherScore = score;
        if (teacher2Score.HasValue) a.Teacher2Score = teacher2Score.Value;
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("TeacherScoreSet", "Answer", answerId, null, new { score, teacher2Score });
        return (await GetAsync(answerId, ct))!;
    }

    public async Task<IReadOnlyList<AnswerDto>> ListByQuestionAsync(Guid questionId, CancellationToken ct = default)
    {
        var answers = await _db.Answers
            .Include(x => x.Student)
            .Include(x => x.GradingRuns)
            .Where(a => a.QuestionId == questionId)
            .OrderBy(a => a.Student.ExternalId)
            .ToListAsync(ct);
        return answers.Select(Map).ToList();
    }

    private static AnswerDto Map(Answer a) => new(
        a.Id, a.StudentId,
        a.Student?.ExternalId ?? a.StudentId.ToString(),
        a.QuestionId, a.ImageStorageKey,
        a.TeacherScore, a.Teacher2Score, a.UploadedAt,
        a.GradingRuns.Select(r => new GradingRunSummaryDto(
            r.Id, r.Provider, r.ModelName, r.PromptVersion,
            r.Temperature, r.AiScore, r.TeacherScoreSnapshot,
            r.IsValid, r.Error, r.CreatedAt)).ToList()
    );
}