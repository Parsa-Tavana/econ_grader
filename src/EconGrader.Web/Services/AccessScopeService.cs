using EconGrader.Application.Data;
using EconGrader.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace EconGrader.Web.Services;

/// <summary>
/// Resource-level authorization checks that role attributes alone can't express:
/// Teacher → own exams (Exams.CreatedByUserId), Corrector → assigned exams
/// (ExamCorrectors), Student → own rows (Students.UserId). Admin bypasses all.
/// Every check throws ResourceAccessDeniedException → 403 on failure.
/// </summary>
public interface IAccessScopeService
{
    /// <summary>Throws unless the user may act on this exam per the role matrix.</summary>
    Task AssertExamAccessAsync(CurrentUser user, Guid examId, bool writeAccess, CancellationToken ct = default);
    /// <summary>Bulk form for list endpoints — returns exam ids the user may see.</summary>
    Task<IReadOnlySet<Guid>> GetAccessibleExamIdsAsync(CurrentUser user, CancellationToken ct = default);

    Task<bool> CanAccessQuestionAsync(CurrentUser user, Guid questionId, bool writeAccess, CancellationToken ct = default);
    Task<bool> CanAccessAnswerAsync(CurrentUser user, Guid answerId, bool writeAccess, CancellationToken ct = default);
    Task<bool> CanAccessRunAsync(CurrentUser user, Guid runId, CancellationToken ct = default);
}

public sealed class AccessScopeService(IAppDbContext db) : IAccessScopeService
{
    public async Task AssertExamAccessAsync(CurrentUser user, Guid examId, bool writeAccess, CancellationToken ct = default)
    {
        if (user.IsAdmin) return;

        if (user.IsTeacher)
        {
            var owner = await db.Exams.Where(e => e.Id == examId)
                .Select(e => (Guid?)e.CreatedByUserId).FirstOrDefaultAsync(ct);
            if (owner == user.UserId) return;
            // Distinguish 404 (no such exam) from 403 (not yours) for clean UX.
            if (owner is null) throw new KeyNotFoundException($"Exam {examId} not found");
            throw new ResourceAccessDeniedException("This exam belongs to another teacher");
        }

        if (user.IsCorrector)
        {
            if (writeAccess)
                throw new ResourceAccessDeniedException("Correctors have no authoring rights on exams");
            var assigned = await db.ExamCorrectors.AnyAsync(ec => ec.CorrectorUserId == user.UserId && ec.ExamId == examId, ct);
            if (!assigned)
            {
                var exists = await db.Exams.AnyAsync(e => e.Id == examId, ct);
                if (!exists) throw new KeyNotFoundException($"Exam {examId} not found");
                throw new ResourceAccessDeniedException("You are not assigned to this exam");
            }
            return;
        }

        if (user.IsStudent)
        {
            if (writeAccess)
                throw new ResourceAccessDeniedException("Students cannot modify exams");
            // Students see only exams that contain a question they answered.
            var linked = await db.Students
                .Where(s => s.UserId == user.UserId)
                .SelectMany(s => s.Answers.Select(a => a.Question.ExamId))
                .AnyAsync(examIdOfAnswer => examIdOfAnswer == examId, ct);
            if (!linked) throw new ResourceAccessDeniedException("You are not enrolled in this exam");
            return;
        }

        throw new ResourceAccessDeniedException("Role not permitted to access exams");
    }

    public async Task<IReadOnlySet<Guid>> GetAccessibleExamIdsAsync(CurrentUser user, CancellationToken ct = default)
    {
        if (user.IsAdmin || user.IsTeacher)
        {
            // Teachers: their own exams. Admins: everything.
            var ids = await db.Exams
                .Where(e => user.IsAdmin || e.CreatedByUserId == user.UserId)
                .Select(e => e.Id).ToListAsync(ct);
            return new HashSet<Guid>(ids);
        }
        if (user.IsCorrector)
            return new HashSet<Guid>(await db.ExamCorrectors
                .Where(ec => ec.CorrectorUserId == user.UserId)
                .Select(ec => ec.ExamId).ToListAsync(ct));

        // Students: exams containing questions they answered.
        return new HashSet<Guid>(await db.Students
            .Where(s => s.UserId == user.UserId)
            .SelectMany(s => s.Answers.Select(a => a.Question.ExamId))
            .Distinct().ToListAsync(ct));
    }

    public async Task<bool> CanAccessQuestionAsync(CurrentUser user, Guid questionId, bool writeAccess, CancellationToken ct = default)
    {
        var examId = await db.Questions.Where(q => q.Id == questionId)
            .Select(q => (Guid?)q.ExamId).FirstOrDefaultAsync(ct);
        if (examId is null) throw new KeyNotFoundException($"Question {questionId} not found");

        try { await AssertExamAccessAsync(user, examId.Value, writeAccess, ct); return true; }
        catch (ResourceAccessDeniedException) { return false; }
        // KeyNotFoundException propagates as 404.
    }

    public async Task<bool> CanAccessAnswerAsync(CurrentUser user, Guid answerId, bool writeAccess, CancellationToken ct = default)
    {
        // Students authorize against their own row, not the exam.
        if (user.IsStudent && !writeAccess)
        {
            var own = await db.Students.AnyAsync(s => s.UserId == user.UserId &&
                s.Answers.Any(a => a.Id == answerId), ct);
            if (!own)
            {
                var exists = await db.Answers.AnyAsync(a => a.Id == answerId, ct);
                if (!exists) throw new KeyNotFoundException($"Answer {answerId} not found");
                throw new ResourceAccessDeniedException("This is another student's answer");
            }
            return true;
        }
        if (user.IsStudent && writeAccess)
            throw new ResourceAccessDeniedException("Students cannot modify answers");

        var info = await db.Answers
            .Where(a => a.Id == answerId)
            .Select(a => new { a.Question.ExamId })
            .FirstOrDefaultAsync(ct);
        if (info is null) throw new KeyNotFoundException($"Answer {answerId} not found");

        try { await AssertExamAccessAsync(user, info.ExamId, writeAccess, ct); return true; }
        catch (ResourceAccessDeniedException) { return false; }
    }

    public async Task<bool> CanAccessRunAsync(CurrentUser user, Guid runId, CancellationToken ct = default)
    {
        if (user.IsStudent)
        {
            var own = await db.GradingRuns.AnyAsync(r => r.Id == runId &&
                r.Answer.Student.UserId == user.UserId, ct);
            if (!own)
            {
                var exists = await db.GradingRuns.AnyAsync(r => r.Id == runId, ct);
                if (!exists) throw new KeyNotFoundException($"Grading run {runId} not found");
                throw new ResourceAccessDeniedException("This grading run is not yours");
            }
            return true;
        }
        var examId = await db.GradingRuns.Where(r => r.Id == runId)
            .Select(r => (Guid?)r.Question.ExamId).FirstOrDefaultAsync(ct);
        if (examId is null) throw new KeyNotFoundException($"Grading run {runId} not found");
        try { await AssertExamAccessAsync(user, examId.Value, writeAccess: false, ct); return true; }
        catch (ResourceAccessDeniedException) { return false; }
    }
}
