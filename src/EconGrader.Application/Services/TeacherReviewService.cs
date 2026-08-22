using EconGrader.Application.Interfaces;

namespace EconGrader.Application.Services;

public interface ITeacherReviewService
{
    Task<TeacherReview> AcceptAsync(Guid runId, Guid teacherUserId, string? note, CancellationToken ct = default);
    Task<TeacherReview> OverrideAsync(Guid runId, Guid teacherUserId, decimal newScore, string? note, CancellationToken ct = default);
    Task<IReadOnlyList<TeacherReviewDto>> GetHistoryAsync(Guid runId, CancellationToken ct = default);
}

public record TeacherReviewDto(
    Guid Id,
    Guid GradingRunId,
    Guid TeacherUserId,
    string TeacherName,
    decimal OldAiScore,
    decimal NewScore,
    string? Note,
    DateTime ReviewedAt,
    ReviewAction Action
);

public sealed class TeacherReviewService : ITeacherReviewService
{
    private readonly IAppDbContext _db;
    private readonly IAuditLogger _audit;

    public TeacherReviewService(IAppDbContext db, IAuditLogger audit) { _db = db; _audit = audit; }

    public async Task<TeacherReview> AcceptAsync(Guid runId, Guid teacherUserId, string? note, CancellationToken ct = default)
    {
        var run = await _db.GradingRuns.FindAsync([runId], ct) ?? throw new InvalidOperationException("Run not found");
        var review = new TeacherReview
        {
            GradingRunId = runId,
            TeacherUserId = teacherUserId,
            OldAiScore = run.AiScore,
            NewScore = run.AiScore,
            Note = note,
            Action = ReviewAction.Accept,
        };
        _db.TeacherReviews.Add(review);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("TeacherAccepted", "TeacherReview", review.Id, teacherUserId, new { runId, run.AiScore });
        return review;
    }

    public async Task<TeacherReview> OverrideAsync(Guid runId, Guid teacherUserId, decimal newScore, string? note, CancellationToken ct = default)
    {
        var run = await _db.GradingRuns.FindAsync([runId], ct) ?? throw new InvalidOperationException("Run not found");
        var review = new TeacherReview
        {
            GradingRunId = runId,
            TeacherUserId = teacherUserId,
            OldAiScore = run.AiScore,
            NewScore = newScore,
            Note = note,
            Action = ReviewAction.Override,
        };
        _db.TeacherReviews.Add(review);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("TeacherOverrode", "TeacherReview", review.Id, teacherUserId, new { runId, Old = run.AiScore, New = newScore });
        return review;
    }

    public async Task<IReadOnlyList<TeacherReviewDto>> GetHistoryAsync(Guid runId, CancellationToken ct = default)
    {
        return await _db.TeacherReviews
            .Include(r => r.Teacher)
            .Where(r => r.GradingRunId == runId)
            .OrderBy(r => r.ReviewedAt)
            .Select(r => new TeacherReviewDto(
                r.Id, r.GradingRunId, r.TeacherUserId, r.Teacher.DisplayName,
                r.OldAiScore, r.NewScore, r.Note, r.ReviewedAt, r.Action))
            .ToListAsync(ct);
    }
}