using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EconGrader.Application.DTOs;
using EconGrader.Application.Interfaces;
using EconGrader.Application.Services;
using EconGrader.Domain.Entities;
using EconGrader.Web.Services;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class QuestionsController : ControllerBase
{
    private readonly IQuestionService _svc;
    private readonly IFileStorage _storage;
    private readonly IArtifactStore _artifacts;
    private readonly IIngestService _ingest;
    private readonly IAppDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly ILogger<QuestionsController> _logger;
    private readonly CurrentUser _user;
    private readonly IAccessScopeService _scope;

    public QuestionsController(IQuestionService svc, IFileStorage storage, IAppDbContext db,
        IArtifactStore artifacts, IIngestService ingest, IAuditLogger audit,
        ILogger<QuestionsController> logger, CurrentUser user, IAccessScopeService scope)
    {
        _svc = svc;
        _storage = storage;
        _artifacts = artifacts;
        _ingest = ingest;
        _db = db;
        _audit = audit;
        _logger = logger;
        _user = user;
        _scope = scope;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<QuestionDto>> Get(Guid id, CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, id, writeAccess: false, ct)) return Forbid();
        return await _svc.GetAsync(id, ct) is { } dto ? Ok(dto) : NotFound();
    }

    [HttpGet("by-exam/{examId:guid}")]
    public async Task<ActionResult<IReadOnlyList<QuestionDto>>> ListByExam(Guid examId, CancellationToken ct)
    {
        await _scope.AssertExamAccessAsync(_user, examId, writeAccess: false, ct);
        return Ok(await _svc.ListByExamAsync(examId, ct));
    }

    [HttpPost]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<ActionResult<QuestionDto>> Create([FromBody] CreateQuestionRequest request, CancellationToken ct)
    {
        await _scope.AssertExamAccessAsync(_user, request.ExamId, writeAccess: true, ct);
        var dto = await _svc.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<ActionResult<QuestionDto>> Update(
        Guid id,
        [FromBody] UpdateQuestionRequest request,
        CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, id, writeAccess: true, ct)) return Forbid();
        return await _svc.UpdateAsync(id, request.Text, request.MaxScore, ct) is { } dto ? Ok(dto) : NotFound();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, id, writeAccess: true, ct)) return Forbid();
        return await _svc.DeleteAsync(id, ct) ? NoContent() : NotFound();
    }

    [HttpGet("{id:guid}/rubric")]
    public async Task<ActionResult<RubricDto>> GetActiveRubric(Guid id, CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, id, writeAccess: false, ct)) return Forbid();
        return await _svc.GetActiveRubricAsync(id, ct) is { } dto ? Ok(dto) : NotFound();
    }

    [HttpPost("{id:guid}/rubrics")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<ActionResult<RubricDto>> CreateRubric(
        Guid id,
        [FromBody] CreateRubricRequest request,
        CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, id, writeAccess: true, ct)) return Forbid();
        // Ensure the rubric targets this question, regardless of body
        var effective = new CreateRubricRequest(id, request.Criteria);
        var dto = await _svc.CreateRubricAsync(effective, _user.UserId, ct);
        return CreatedAtAction(nameof(GetActiveRubric), new { id }, dto);
    }

    /// <summary>
    /// Upload/replace the question paper file (PDF/PNG/JPG/DOCX/XLSX/XLS, ≤20 MB).
    /// Replaces any previously stored file for this question.
    /// </summary>
    [HttpPost("{id:guid}/file")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    [RequestSizeLimit(FileUploadValidator.MaxBytes)]
    public async Task<IActionResult> UploadFile(Guid id, IFormFile file, CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, id, writeAccess: true, ct)) return Forbid();
        if (file.Length == 0) return BadRequest(new { code = "EMPTY_FILE", message = "Empty file" });
        var ext = FileUploadValidator.ExtensionForFile(file.ContentType, file.FileName);
        if (ext is null)
            return StatusCode(415, new { code = "UNSUPPORTED_MEDIA_TYPE", message = $"Unsupported file type '{file.ContentType}' — use {FileUploadValidator.AcceptedTypesDisplay}" });

        var question = await _db.Questions.FindAsync([id], ct);
        if (question is null) return NotFound();

        // Content-addressed original artifact (M1) — deduped, shared.
        var artifact = await _artifacts.SaveOriginalAsync(file.OpenReadStream(), file.FileName, file.ContentType, ct);

        // Delete the previous file only when it is a legacy (non-shared) key.
        if (!string.IsNullOrEmpty(question.FileStorageKey) && !IsContentAddressed(question.FileStorageKey!))
        {
            try { await _storage.DeleteAsync(question.FileStorageKey, ct); }
            catch (IOException ex) { _logger.LogWarning(ex, "Could not delete previous question file {Key}", question.FileStorageKey); }
        }

        question.FileStorageKey = artifact.StorageKey;
        question.FileArtifactId = artifact.Id;
        question.FileName = Path.GetFileName(file.FileName); // strip any path segments
        question.ContentType = file.ContentType;
        await _db.SaveChangesAsync(ct);

        // Ingest renders pages once; grading consumes them (legacy fallback if it fails).
        try { await _ingest.EnsureIngestedAsync(artifact.Id, "question", ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Post-upload ingest failed for question file artifact {ArtifactId}", artifact.Id); }

        _logger.LogInformation("Question file uploaded {QuestionId} {FileName} ({Bytes} bytes)", id, question.FileName, file.Length);
        return Ok(new { fileStorageKey = question.FileStorageKey, artifactId = artifact.Id, fileName = question.FileName, contentType = question.ContentType });
    }

    /// <summary>True when a storage key lives in the content-addressed blob
    /// tree (shared, deduped — never safe to delete on replace).</summary>
    private static bool IsContentAddressed(string key) =>
        key.StartsWith("blobs/", StringComparison.Ordinal) || key.StartsWith("pages/", StringComparison.Ordinal);

    /// <summary>Download/stream the stored question paper file.</summary>
    [HttpGet("{id:guid}/file")]
    public async Task<IActionResult> DownloadFile(Guid id, CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, id, writeAccess: false, ct)) return Forbid();
        var question = await _db.Questions.FindAsync([id], ct);
        if (question is null || string.IsNullOrEmpty(question.FileStorageKey)) return NotFound();

        var stream = await _storage.OpenReadAsync(question.FileStorageKey, ct);
        if (stream is null) return NotFound();

        return File(stream, question.ContentType ?? "application/octet-stream",
            question.FileName ?? $"question-{id}");
    }

    /// <summary>Remove the stored question paper file.</summary>
    [HttpDelete("{id:guid}/file")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<IActionResult> DeleteFile(Guid id, CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, id, writeAccess: true, ct)) return Forbid();
        var question = await _db.Questions.FindAsync([id], ct);
        if (question is null) return NotFound();
        if (string.IsNullOrEmpty(question.FileStorageKey)) return NoContent();

        if (!IsContentAddressed(question.FileStorageKey!))
        {
            try { await _storage.DeleteAsync(question.FileStorageKey, ct); }
            catch (IOException ex) { _logger.LogWarning(ex, "Could not delete question file {Key}", question.FileStorageKey); }
        }

        question.FileStorageKey = null;
        question.FileArtifactId = null;
        question.FileName = null;
        question.ContentType = null;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── پاسخنامه (model answer / examiner's key) — M1, grading-inert by default ──

    /// <summary>
    /// Upload/replace the پاسخنامه (model answer) file for this question
    /// (same validators as the question file). Stored as a content-addressed
    /// artifact + ingested to pages. GRADING-INERT: the grader only receives
    /// these pages when Exam.GroundTruthGradingEnabled is ON (M4 gate).
    /// </summary>
    [HttpPost("{id:guid}/answerkey")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    [RequestSizeLimit(FileUploadValidator.MaxBytes)]
    public async Task<IActionResult> UploadAnswerKey(Guid id, IFormFile file, CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, id, writeAccess: true, ct)) return Forbid();
        if (file.Length == 0) return BadRequest(new { code = "EMPTY_FILE", message = "Empty file" });
        var ext = FileUploadValidator.ExtensionForFile(file.ContentType, file.FileName);
        if (ext is null)
            return StatusCode(415, new { code = "UNSUPPORTED_MEDIA_TYPE", message = $"Unsupported file type '{file.ContentType}' — use {FileUploadValidator.AcceptedTypesDisplay}" });

        var question = await _db.Questions.FindAsync([id], ct);
        if (question is null) return NotFound();

        var artifact = await _artifacts.SaveOriginalAsync(file.OpenReadStream(), file.FileName, file.ContentType, ct);

        if (!string.IsNullOrEmpty(question.AnswerKeyStorageKey) && !IsContentAddressed(question.AnswerKeyStorageKey!))
        {
            try { await _storage.DeleteAsync(question.AnswerKeyStorageKey, ct); }
            catch (IOException ex) { _logger.LogWarning(ex, "Could not delete previous answer-key file {Key}", question.AnswerKeyStorageKey); }
        }

        question.AnswerKeyStorageKey = artifact.StorageKey;
        question.AnswerKeyArtifactId = artifact.Id;
        question.AnswerKeyFileName = Path.GetFileName(file.FileName);
        question.AnswerKeyContentType = file.ContentType;
        await _db.SaveChangesAsync(ct);

        try { await _ingest.EnsureIngestedAsync(artifact.Id, "answerkey", ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Post-upload ingest failed for answer-key artifact {ArtifactId}", artifact.Id); }

        await _audit.WriteAsync("QuestionAnswerKeyUploaded", "Question", id, _user.UserId,
            new { artifactId = artifact.Id, question.AnswerKeyFileName });
        _logger.LogInformation("Answer key uploaded for question {QuestionId} ({FileName})", id, question.AnswerKeyFileName);
        return Ok(new { artifactId = artifact.Id, fileName = question.AnswerKeyFileName, contentType = question.AnswerKeyContentType });
    }

    /// <summary>Remove the پاسخنامه file for this question.</summary>
    [HttpDelete("{id:guid}/answerkey")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<IActionResult> DeleteAnswerKey(Guid id, CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, id, writeAccess: true, ct)) return Forbid();
        var question = await _db.Questions.FindAsync([id], ct);
        if (question is null) return NotFound();
        if (string.IsNullOrEmpty(question.AnswerKeyStorageKey)) return NoContent();

        if (!IsContentAddressed(question.AnswerKeyStorageKey!))
        {
            try { await _storage.DeleteAsync(question.AnswerKeyStorageKey, ct); }
            catch (IOException ex) { _logger.LogWarning(ex, "Could not delete answer-key file {Key}", question.AnswerKeyStorageKey); }
        }

        question.AnswerKeyStorageKey = null;
        question.AnswerKeyArtifactId = null;
        question.AnswerKeyFileName = null;
        question.AnswerKeyContentType = null;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync("QuestionAnswerKeyDeleted", "Question", id, _user.UserId, null);
        return NoContent();
    }

    /// <summary>Download/stream the stored پاسخنامه file.</summary>
    [HttpGet("{id:guid}/answerkey")]
    public async Task<IActionResult> DownloadAnswerKey(Guid id, CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, id, writeAccess: false, ct)) return Forbid();
        var question = await _db.Questions.FindAsync([id], ct);
        if (question is null || string.IsNullOrEmpty(question.AnswerKeyStorageKey)) return NotFound();

        var stream = await _storage.OpenReadAsync(question.AnswerKeyStorageKey, ct);
        if (stream is null) return NotFound();

        return File(stream, question.AnswerKeyContentType ?? "application/octet-stream",
            question.AnswerKeyFileName ?? $"answerkey-{id}");
    }
}

public record UpdateQuestionRequest(string? Text, decimal? MaxScore);