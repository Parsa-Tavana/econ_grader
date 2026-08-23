using EconGrader.Application.Exceptions;
using EconGrader.Application.Interfaces;
using EconGrader.Domain.Entities;

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

/// <summary>
/// Append-only teacher review trail. Accept keeps AI score; Override replaces it.
/// Never mutates past reviews — every decision is recorded.
/// </summary>
public sealed class TeacherReviewService : ITeacherReviewService
{
    private readonly IAppDbContext _db;
    private readonly IAuditLogger _audit;

    public TeacherReviewService(IAppDbContext db, IAuditLogger audit) { _db = db; _audit = audit; }

    public Task<TeacherReview> AcceptAsync(Guid runId, Guid teacherUserId, string? note, CancellationToken ct = default) =>
        CreateReviewAsync(runId, teacherUserId, ReviewAction.Accept, score => score, note, "TeacherAccepted", ct);

    public Task<TeacherReview> OverrideAsync(Guid runId, Guid teacherUserId, decimal newScore, string? note, CancellationToken ct = default) =>
        CreateReviewAsync(runId, teacherUserId, ReviewAction.Override, _ => newScore, note, "TeacherOverrode", ct);

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

    /// <summary>Single code path for both actions — differs only by resolved new-score and audit verb.</summary>
    private async Task<TeacherReview> CreateReviewAsync(
        Guid runId, Guid teacherUserId, ReviewAction action,
        Func<decimal, decimal> resolveNewScore, string? note, string auditAction, CancellationToken ct)
    {
        var run = await _db.GradingRuns.FindAsync([runId], ct)
            ?? throw new NotFoundException(nameof(GradingRun), runId);

        // Attribution-only identity: auto-provision placeholder user so the
        // TeacherReviews.TeacherUserId FK is satisfied for any valid GUID.
        await _db.EnsureUserAsync(teacherUserId, ct);

        var review = new TeacherReview
        {
            GradingRunId = runId,
            TeacherUserId = teacherUserId,
            OldAiScore = run.AiScore,
            NewScore = resolveNewScore(run.AiScore),
            Note = note,
            Action = action,
        };
        _db.TeacherReviews.Add(review);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync(auditAction, nameof(TeacherReview), review.Id, teacherUserId, new
        {
            runId,
            Old = review.OldAiScore,
            New = review.NewScore,
        });
        return review;
    }
}