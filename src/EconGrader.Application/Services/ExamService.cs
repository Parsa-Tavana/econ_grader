using EconGrader.Application.DTOs;
using EconGrader.Application.Exceptions;
using EconGrader.Domain.Entities;

namespace EconGrader.Application.Services;

public interface IExamService
{
    Task<ExamDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ExamDto>> ListAsync(CancellationToken ct = default);
    Task<ExamDto> CreateAsync(CreateExamRequest request, Guid createdByUserId, CancellationToken ct = default);
    Task<ExamDto?> UpdateAsync(Guid id, UpdateExamRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

public sealed class ExamService : IExamService
{
    private readonly IAppDbContext _db;
    private readonly IAuditLogger _audit;

    public ExamService(IAppDbContext db, IAuditLogger audit) { _db = db; _audit = audit; }

    public async Task<ExamDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var e = await _db.Exams
            .Include(x => x.CreatedBy)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? null : Map(e);
    }

    public async Task<IReadOnlyList<ExamDto>> ListAsync(CancellationToken ct = default)
    {
        return await _db.Exams
            .Include(x => x.CreatedBy)
            .OrderByDescending(x => x.Year).ThenBy(x => x.Name)
            .Select(e => Map(e))
            .ToListAsync(ct);
    }

    public async Task<ExamDto> CreateAsync(CreateExamRequest request, Guid createdByUserId, CancellationToken ct = default)
    {
        // createdByUserId comes from the validated JWT (never a header) — the
        // account must already exist; FK violation would indicate a bug upstream.
        var exam = new Exam { Name = request.Name, Year = request.Year, Description = request.Description, CreatedByUserId = createdByUserId };
        _db.Exams.Add(exam);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("ExamCreated", "Exam", exam.Id, createdByUserId, new { exam.Name, exam.Year });
        return await GetAsync(exam.Id, ct) ?? throw new NotFoundException(nameof(Exam), exam.Id);
    }

    public async Task<ExamDto?> UpdateAsync(Guid id, UpdateExamRequest request, CancellationToken ct = default)
    {
        var exam = await _db.Exams.FindAsync([id], ct);
        if (exam is null) return null;
        exam.Name = request.Name;
        exam.Year = request.Year;
        exam.Description = request.Description;
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("ExamUpdated", "Exam", exam.Id, null, new { exam.Name, exam.Year });
        return await GetAsync(exam.Id, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var exam = await _db.Exams.FindAsync([id], ct);
        if (exam is null) return false;
        _db.Exams.Remove(exam);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("ExamDeleted", "Exam", exam.Id, null);
        return true;
    }

    private static ExamDto Map(Exam e) => new(e.Id, e.Name, e.Year, e.Description, e.CreatedAt, e.CreatedBy?.DisplayName ?? "unknown");
}