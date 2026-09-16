using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EconGrader.Application.Interfaces;
using EconGrader.Application.Services;
using EconGrader.Domain.Entities;
using EconGrader.Web.Services;

namespace EconGrader.Web.Controllers;

/// <summary>
/// M6 bulk answer-sheet split: upload ONE merged multi-student PDF, the
/// system splits it into per-student answers via header-band OCR, the teacher
/// reviews/fixes the grouping in the UI, and confirm applies real Answer rows
/// with single-upload semantics.
///
/// All routes teacher-only (Admin included via the Teacher role check) and
/// scoped to question-level write access. Safety: unknown OCR ids never
/// auto-create Students — mapping to students happens here, explicitly.
/// </summary>
[ApiController]
[Route("api/answers/bulk")]
[Authorize]
public sealed class BulkAnswersController : ControllerBase
{
    private readonly ISplitService _split;
    private readonly IArtifactStore _artifacts;
    private readonly IIngestService _ingest;
    private readonly IAppDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly IAccessScopeService _scope;
    private readonly CurrentUser _user;
    private readonly ILogger<BulkAnswersController> _logger;

    public BulkAnswersController(ISplitService split, IArtifactStore artifacts,
        IIngestService ingest, IAppDbContext db, IAuditLogger audit,
        IAccessScopeService scope, CurrentUser user, ILogger<BulkAnswersController> logger)
    {
        _split = split;
        _artifacts = artifacts;
        _ingest = ingest;
        _db = db;
        _audit = audit;
        _scope = scope;
        _user = user;
        _logger = logger;
    }

    public sealed record BulkBatchDto(
        Guid Id,
        Guid QuestionId,
        Guid SourceArtifactId,
        string? SourceFileName,
        string Status,
        int TotalPages,
        string? Error,
        DateTime CreatedAt,
        IReadOnlyList<BulkPageDto> Pages);

    public sealed record BulkPageDto(
        Guid Id,
        int PageNumber,
        Guid PageArtifactId,
        string? RawOcrId,
        decimal OcrConfidence,
        Guid? MatchedStudentId,
        string? MatchedStudentExternalId,
        string? MatchedStudentDisplayName,
        string ReviewStatus,
        int SortInStack);

    private static BulkBatchDto ToDto(BulkAnswerBatch b, List<BulkPageMapping> pages) => new(
        b.Id, b.QuestionId, b.SourceArtifactId, b.SourceFileName, b.Status, b.TotalPages,
        b.Error, b.CreatedAt,
        pages.OrderBy(p => p.PageNumber).Select(p => new BulkPageDto(
            p.Id, p.PageNumber, p.PageArtifactId, p.RawOcrId, p.OcrConfidence,
            p.MatchedStudentId,
            p.MatchedStudent?.ExternalId,
            p.MatchedStudent?.DisplayName,
            p.ReviewStatus, p.SortInStack)).ToList());

    /// <summary>
    /// Upload ONE merged multi-student answer sheet (PDF/PNG/JPG, ≤20 MB) for
    /// one question. Content-addressed + idempotent: the same file re-uploaded
    /// returns the SAME batch (no re-OCR). The split runs in the background —
    /// poll GET /{id} until ready_for_review.
    /// </summary>
    [HttpPost("upload")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    [RequestSizeLimit(FileUploadValidator.MaxBytes)]
    public async Task<ActionResult<BulkBatchDto>> Upload(
        [FromForm] Guid questionId,
        IFormFile file,
        CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, questionId, writeAccess: true, ct)) return Forbid();
        if (file.Length == 0) return BadRequest(new { code = "EMPTY_FILE", message = "Empty file" });
        var ext = FileUploadValidator.ExtensionForFile(file.ContentType, file.FileName);
        if (ext is null)
            return StatusCode(415, new { code = "UNSUPPORTED_MEDIA_TYPE", message = $"Unsupported file type '{file.ContentType}' — use {FileUploadValidator.AcceptedTypesDisplay}" });
        // Only paginated/scanned formats make sense for a merged sheet.
        if (ext is not (".pdf" or ".png" or ".jpg"))
            return BadRequest(new { code = "UNSUPPORTED_BULK_FORMAT", message = "Merged answer sheets must be PDF, PNG or JPG (not Office documents)" });

        if (await _db.Questions.FindAsync([questionId], ct) is null)
            return NotFound(new { code = "UNKNOWN_QUESTION", message = $"Question {questionId} not found" });

        // Content-addressed original artifact — identical re-uploads dedup.
        var artifact = await _artifacts.SaveOriginalAsync(file.OpenReadStream(), file.FileName, file.ContentType, ct);

        var batch = await _split.EnqueueAsync(questionId, artifact.Id,
            Path.GetFileName(file.FileName), file.ContentType, _user.UserId, ct);
        await _audit.WriteAsync("BulkBatchUploaded", "BulkAnswerBatch", batch.Id, _user.UserId,
            new { questionId, artifactId = artifact.Id, fileName = Path.GetFileName(file.FileName) });

        var dto = await ProjectBatchAsync(batch.Id, ct);
        return dto is null
            ? Created($"api/answers/bulk/{batch.Id}", new { batch.Id })
            : Ok(dto);
    }

    /// <summary>Batch review state: pages grouped per student with OCR ids and
    /// confidences. The UI renders thumbnails via /pages/{artifactId}/image.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<ActionResult<BulkBatchDto>> Get(Guid id, CancellationToken ct)
    {
        var batch = await _db.BulkAnswerBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return NotFound();
        if (!await _scope.CanAccessQuestionAsync(_user, batch.QuestionId, writeAccess: false, ct)) return Forbid();
        return Ok(await ProjectBatchAsync(id, ct));
    }

    private async Task<BulkBatchDto?> ProjectBatchAsync(Guid batchId, CancellationToken ct)
    {
        var batch = await _db.BulkAnswerBatches
            .Include(b => b.SourceArtifact)
            .FirstOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null) return null;
        var pages = await _db.BulkPageMappings
            .Include(p => p.MatchedStudent)
            .Where(p => p.BatchId == batchId)
            .ToListAsync(ct);
        return ToDto(batch, pages);
    }

    /// <summary>Fix ONE page from the review UI: assign to a student (reassign
    /// / map an unknown id), mark confirmed, or mark skipped (blank/trash page).
    /// Unknown ids NEVER auto-create Students — pass a known studentId, or
    /// create the student first via POST /api/students.</summary>
    [HttpPatch("{id:guid}/pages/{pageId:guid}")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<ActionResult<BulkPageDto>> UpdatePage(
        Guid id, Guid pageId, [FromBody] UpdateBulkPageRequest request, CancellationToken ct)
    {
        var batch = await _db.BulkAnswerBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return NotFound();
        if (!await _scope.CanAccessQuestionAsync(_user, batch.QuestionId, writeAccess: true, ct)) return Forbid();
        if (batch.Status is "applied")
            return BadRequest(new { code = "BULK_BAD_STATE", message = "Batch already applied — pages are frozen" });

        var page = await _db.BulkPageMappings.FirstOrDefaultAsync(p => p.Id == pageId && p.BatchId == id, ct);
        if (page is null) return NotFound();

        if (request.StudentId is { } sid)
        {
            if (await _db.Students.FindAsync([sid], ct) is null)
                return BadRequest(new { code = "UNKNOWN_STUDENT", message = $"Unknown student {sid}" });
            page.MatchedStudentId = sid;
            page.ReviewStatus = "confirmed";
        }
        else if (request.Confirm is true)
        {
            page.ReviewStatus = "confirmed";
        }
        else if (request.Skip is true)
        {
            page.ReviewStatus = "skipped";
            page.MatchedStudentId = null;
        }
        else if (request.Clear is true)
        {
            page.ReviewStatus = "unmatched";
            page.MatchedStudentId = null;
        }
        else
        {
            return BadRequest(new { code = "EMPTY_UPDATE", message = "Provide studentId, confirm, skip or clear" });
        }

        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("BulkPageUpdated", "BulkAnswerBatch", id, _user.UserId,
            new { pageId, page.ReviewStatus, studentId = page.MatchedStudentId });
        return Ok(new BulkPageDto(
            page.Id, page.PageNumber, page.PageArtifactId, page.RawOcrId, page.OcrConfidence,
            page.MatchedStudentId,
            page.MatchedStudent?.ExternalId,
            page.MatchedStudent?.DisplayName,
            page.ReviewStatus, page.SortInStack));
    }

    public sealed class UpdateBulkPageRequest
    {
        /// <summary>Assign/reassign this page to a student (marks confirmed).</summary>
        public Guid? StudentId { get; set; }
        /// <summary>Keep the OCR assignment but mark it teacher-confirmed.</summary>
        public bool? Confirm { get; set; }
        /// <summary>Exclude this page from apply (blank/trash page).</summary>
        public bool? Skip { get; set; }
        /// <summary>Undo an assignment — back to unmatched.</summary>
        public bool? Clear { get; set; }
    }

    /// <summary>Confirm → apply. Creates/replaces Answer rows per student from
    /// the auto + confirmed pages; needs_review/unmatched pages are left
    /// unassigned (never silently applied — locked safety rule).</summary>
    [HttpPost("{id:guid}/apply")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<IActionResult> Apply(Guid id, CancellationToken ct)
    {
        var batch = await _db.BulkAnswerBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return NotFound();
        if (!await _scope.CanAccessQuestionAsync(_user, batch.QuestionId, writeAccess: true, ct)) return Forbid();

        var summary = await _split.ApplyAsync(id, _user.UserId, ct);
        return Ok(summary);
    }

    /// <summary>Page thumbnail for the review grid (authenticated stream).
    /// Authorization: the artifact must be a page of a batch whose question
    /// the caller can read.</summary>
    [HttpGet("pages/{artifactId:guid}/image")]
    public async Task<IActionResult> PageImage(Guid artifactId, CancellationToken ct)
    {
        var artifact = await _artifacts.GetAsync(artifactId, ct);
        if (artifact is null) return NotFound();

        var batchId = await _db.BulkPageMappings
            .Where(p => p.PageArtifactId == artifactId)
            .Select(p => (Guid?)p.BatchId)
            .FirstOrDefaultAsync(ct);
        if (batchId is null) return NotFound();

        var questionId = await _db.BulkAnswerBatches
            .Where(b => b.Id == batchId.Value)
            .Select(b => (Guid?)b.QuestionId)
            .FirstOrDefaultAsync(ct);
        if (questionId is null ||
            !await _scope.CanAccessQuestionAsync(_user, questionId.Value, writeAccess: false, ct))
            return Forbid();

        var stream = await _artifacts.OpenReadAsync(artifact.StorageKey, ct);
        if (stream is null) return NotFound();
        var contentType = artifact.StorageKey.EndsWith(".png", StringComparison.Ordinal)
            ? "image/png" : "image/jpeg";
        return File(stream, contentType); // inline — thumbnails
    }
}
