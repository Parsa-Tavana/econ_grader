using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EconGrader.Application.Data;
using EconGrader.Application.DTOs;
using EconGrader.Application.Interfaces;
using EconGrader.Application.Services;
using EconGrader.Domain.Entities;
using EconGrader.Web.Services;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class ExamsController : ControllerBase
{
    private readonly IExamService _svc;
    private readonly IAccessScopeService _scope;
    private readonly CurrentUser _user;
    private readonly IAppDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IAuditLogger _audit;
    private readonly IExamExtractionService _extraction;
    private readonly ILogger<ExamsController> _logger;

    public ExamsController(
        IExamService svc, IAccessScopeService scope, CurrentUser user, IAppDbContext db,
        IFileStorage storage, IAuditLogger audit, IExamExtractionService extraction,
        ILogger<ExamsController> logger)
    {
        _svc = svc;
        _scope = scope;
        _user = user;
        _db = db;
        _storage = storage;
        _audit = audit;
        _extraction = extraction;
        _logger = logger;
    }

    /// <summary>Single exam. Teachers see their own; correctors assigned ones;
    /// students ones they answered in.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ExamDto>> Get(Guid id, CancellationToken ct)
    {
        await _scope.AssertExamAccessAsync(_user, id, writeAccess: false, ct);
        return await _svc.GetAsync(id, ct) is { } dto ? Ok(dto) : NotFound();
    }

    /// <summary>Admin: all exams. Teacher: own exams. Corrector: assigned exams.
    /// Student: exams containing their answers.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ExamDto>>> List(CancellationToken ct)
    {
        if (_user.IsAdmin)
            return Ok(await _svc.ListAsync(ct));

        var accessible = await _scope.GetAccessibleExamIdsAsync(_user, ct);
        var all = await _svc.ListAsync(ct);
        return Ok(all.Where(e => accessible.Contains(e.Id)).ToList());
    }

    [HttpPost]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<ActionResult<ExamDto>> Create(
        [FromBody] CreateExamRequest request,
        CancellationToken ct)
    {
        // Ownership is stamped from the token — never from the body/headers.
        var dto = await _svc.CreateAsync(request, _user.UserId, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<ActionResult<ExamDto>> Update(
        Guid id,
        [FromBody] UpdateExamRequest request,
        CancellationToken ct)
    {
        await _scope.AssertExamAccessAsync(_user, id, writeAccess: true, ct);
        return await _svc.UpdateAsync(id, request, ct) is { } dto ? Ok(dto) : NotFound();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _scope.AssertExamAccessAsync(_user, id, writeAccess: true, ct);
        return await _svc.DeleteAsync(id, ct) ? NoContent() : NotFound();
    }

    // ── Exam-wide rubric file (the grading key extraction source) ───────────

    /// <summary>
    /// Upload/replace the exam-wide rubric document — the grading key the AI
    /// extracts ALL questions + their rubric criteria from (PDF/PNG/JPG/DOCX/
    /// XLSX/XLS, ≤20 MB). Stored on the exam, not per question.
    /// </summary>
    [HttpPost("{id:guid}/rubric/file")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    [RequestSizeLimit(FileUploadValidator.MaxBytes)]
    public async Task<IActionResult> UploadRubricFile(Guid id, IFormFile file, CancellationToken ct)
    {
        await _scope.AssertExamAccessAsync(_user, id, writeAccess: true, ct);
        if (file.Length == 0) return BadRequest(new { code = "EMPTY_FILE", message = "Empty file" });
        var ext = FileUploadValidator.ExtensionForFile(file.ContentType, file.FileName);
        if (ext is null)
            return StatusCode(415, new { code = "UNSUPPORTED_MEDIA_TYPE", message = $"Unsupported file type '{file.ContentType}' — use {FileUploadValidator.AcceptedTypesDisplay}" });

        var exam = await _db.Exams.FindAsync([id], ct);
        if (exam is null) return NotFound();

        // Delete the previous file to avoid orphaned blobs.
        if (!string.IsNullOrEmpty(exam.RubricFileStorageKey))
        {
            try { await _storage.DeleteAsync(exam.RubricFileStorageKey, ct); }
            catch (IOException ex) { _logger.LogWarning(ex, "Could not delete previous exam rubric file {Key}", exam.RubricFileStorageKey); }
        }

        var key = $"rubrics/exams/{id:N}/{Guid.NewGuid():N}{ext}";
        await using var stream = file.OpenReadStream();
        await _storage.SaveAsync(stream, key, ct);

        exam.RubricFileStorageKey = key;
        exam.RubricFileName = Path.GetFileName(file.FileName); // strip any path segments
        exam.RubricFileContentType = file.ContentType;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync("ExamRubricFileUploaded", "Exam", id, _user.UserId, new { exam.RubricFileName, bytes = file.Length }, cancellationToken: ct);
        _logger.LogInformation("Exam rubric file uploaded {ExamId} {FileName} ({Bytes} bytes)", id, exam.RubricFileName, file.Length);
        return Ok(new { fileName = exam.RubricFileName, contentType = exam.RubricFileContentType });
    }

    /// <summary>Download/stream the stored exam-wide rubric document.</summary>
    [HttpGet("{id:guid}/rubric/file")]
    public async Task<IActionResult> DownloadRubricFile(Guid id, CancellationToken ct)
    {
        await _scope.AssertExamAccessAsync(_user, id, writeAccess: false, ct);
        var exam = await _db.Exams.FindAsync([id], ct);
        if (exam is null || string.IsNullOrEmpty(exam.RubricFileStorageKey)) return NotFound();

        var stream = await _storage.OpenReadAsync(exam.RubricFileStorageKey, ct);
        if (stream is null) return NotFound();

        return File(stream, exam.RubricFileContentType ?? "application/octet-stream",
            exam.RubricFileName ?? $"exam-rubric-{id}");
    }

    /// <summary>Remove the exam-wide rubric document. Extracted questions are
    /// NOT touched — re-extraction just needs a file uploaded again.</summary>
    [HttpDelete("{id:guid}/rubric/file")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<IActionResult> DeleteRubricFile(Guid id, CancellationToken ct)
    {
        await _scope.AssertExamAccessAsync(_user, id, writeAccess: true, ct);
        var exam = await _db.Exams.FindAsync([id], ct);
        if (exam is null) return NotFound();
        if (string.IsNullOrEmpty(exam.RubricFileStorageKey)) return NoContent();

        try { await _storage.DeleteAsync(exam.RubricFileStorageKey, ct); }
        catch (IOException ex) { _logger.LogWarning(ex, "Could not delete exam rubric file {Key}", exam.RubricFileStorageKey); }

        exam.RubricFileStorageKey = null;
        exam.RubricFileName = null;
        exam.RubricFileContentType = null;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync("ExamRubricFileDeleted", "Exam", id, _user.UserId, null, cancellationToken: ct);
        return NoContent();
    }

    // ── AI extraction: rubric file → questions + rubric criteria ────────────

    /// <summary>
    /// Run AI extraction over the exam's rubric file and return the result as
    /// an EDITABLE PREVIEW. Saves nothing — confirm via POST /extraction/apply.
    /// </summary>
    [HttpPost("{id:guid}/extraction/preview")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<ActionResult<ExtractionPreviewDto>> ExtractionPreview(Guid id, CancellationToken ct)
    {
        await _scope.AssertExamAccessAsync(_user, id, writeAccess: true, ct); // extraction burns tokens — owners only
        return Ok(await _extraction.ExtractPreviewAsync(id, ct));
    }

    /// <summary>
    /// Persist confirmed extraction rows: questions matching an existing number
    /// are updated (changed rubrics become a new version), missing numbers are
    /// created; questions NOT in the payload are left untouched.
    /// </summary>
    [HttpPost("{id:guid}/extraction/apply")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<ActionResult<ApplyExtractionResultDto>> ExtractionApply(
        Guid id, [FromBody] ApplyExtractionRequest request, CancellationToken ct)
    {
        await _scope.AssertExamAccessAsync(_user, id, writeAccess: true, ct);
        return Ok(await _extraction.ApplyAsync(id, request, _user.UserId, ct));
    }

    // ── Corrector assignment (exam owner / admin only) ──────────────────────

    /// <summary>Assign a corrector to this exam. Only the owning teacher or an
    /// admin may assign; the target must be an active Role=Corrector account.</summary>
    [HttpPost("{id:guid}/correctors")]
    [Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.Teacher)}")]
    public async Task<IActionResult> AssignCorrector(
        Guid id, [FromBody] AssignCorrectorRequest body, CancellationToken ct)
    {
        if (!_user.IsAdmin)
            await _scope.AssertExamAccessAsync(_user, id, writeAccess: true, ct);

        var corrector = await _db.Users.FirstOrDefaultAsync(u => u.Id == body.CorrectorUserId, ct);
        if (corrector is null) return NotFound(new { code = "USER_NOT_FOUND", message = $"No user {body.CorrectorUserId}" });
        if (corrector.Role != UserRole.Corrector || !corrector.IsActive)
            return BadRequest(new { code = "NOT_A_CORRECTOR", message = "Target must be an active account with Role=Corrector" });
        if (!await _db.Exams.AnyAsync(e => e.Id == id, ct))
            return NotFound();

        var added = await _db.ExamCorrectors
            .Where(ec => ec.ExamId == id && ec.CorrectorUserId == body.CorrectorUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(ec => ec.AssignedAt, DateTime.UtcNow), ct);
        if (added == 0)
        {
            _db.ExamCorrectors.Add(new ExamCorrector
            {
                ExamId = id,
                CorrectorUserId = body.CorrectorUserId,
                AssignedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    /// <summary>Remove a corrector from this exam (owner teacher or admin).</summary>
    [HttpDelete("{id:guid}/correctors/{correctorUserId:guid}")]
    [Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.Teacher)}")]
    public async Task<IActionResult> UnassignCorrector(
        Guid id, Guid correctorUserId, CancellationToken ct)
    {
        if (!_user.IsAdmin)
            await _scope.AssertExamAccessAsync(_user, id, writeAccess: true, ct);

        var removed = await _db.ExamCorrectors
            .Where(ec => ec.ExamId == id && ec.CorrectorUserId == correctorUserId)
            .ExecuteDeleteAsync(ct);
        return removed > 0 ? NoContent() : NotFound();
    }

    /// <summary>List correctors assigned to this exam — visible to the owning
    /// teacher and assigned correctors.</summary>
    [HttpGet("{id:guid}/correctors")]
    public async Task<IActionResult> ListCorrectors(Guid id, CancellationToken ct)
    {
        await _scope.AssertExamAccessAsync(_user, id, writeAccess: false, ct);
        var rows = await _db.ExamCorrectors
            .Where(ec => ec.ExamId == id)
            .Join(_db.Users, ec => ec.CorrectorUserId, u => u.Id,
                (ec, u) => new CorrectorAssignmentDto(u.Id, u.Email, u.DisplayName, ec.AssignedAt))
            .ToListAsync(ct);
        return Ok(rows);
    }
}

public record AssignCorrectorRequest(Guid CorrectorUserId);
public record CorrectorAssignmentDto(Guid UserId, string Email, string DisplayName, DateTime AssignedAt);