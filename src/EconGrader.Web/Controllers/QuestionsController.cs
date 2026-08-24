using Microsoft.AspNetCore.Mvc;
using EconGrader.Application.DTOs;
using EconGrader.Application.Interfaces;
using EconGrader.Application.Services;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class QuestionsController : ControllerBase
{
    private readonly IQuestionService _svc;
    private readonly IFileStorage _storage;
    private readonly IAppDbContext _db;
    private readonly ILogger<QuestionsController> _logger;

    public QuestionsController(IQuestionService svc, IFileStorage storage, IAppDbContext db, ILogger<QuestionsController> logger)
    {
        _svc = svc;
        _storage = storage;
        _db = db;
        _logger = logger;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<QuestionDto>> Get(Guid id, CancellationToken ct) =>
        await _svc.GetAsync(id, ct) is { } dto ? Ok(dto) : NotFound();

    [HttpGet("by-exam/{examId:guid}")]
    public async Task<ActionResult<IReadOnlyList<QuestionDto>>> ListByExam(Guid examId, CancellationToken ct) =>
        Ok(await _svc.ListByExamAsync(examId, ct));

    [HttpPost]
    public async Task<ActionResult<QuestionDto>> Create([FromBody] CreateQuestionRequest request, CancellationToken ct)
    {
        var dto = await _svc.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<QuestionDto>> Update(
        Guid id,
        [FromBody] UpdateQuestionRequest request,
        CancellationToken ct) =>
        await _svc.UpdateAsync(id, request.Text, request.MaxScore, request.RubricText, ct) is { } dto ? Ok(dto) : NotFound();

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await _svc.DeleteAsync(id, ct) ? NoContent() : NotFound();

    [HttpGet("{id:guid}/rubric")]
    public async Task<ActionResult<RubricDto>> GetActiveRubric(Guid id, CancellationToken ct) =>
        await _svc.GetActiveRubricAsync(id, ct) is { } dto ? Ok(dto) : NotFound();

    [HttpPost("{id:guid}/rubrics")]
    public async Task<ActionResult<RubricDto>> CreateRubric(
        Guid id,
        [FromBody] CreateRubricRequest request,
        [FromHeader(Name = "X-User-Id")] Guid createdByUserId,
        CancellationToken ct)
    {
        // Ensure the rubric targets this question, regardless of body
        var effective = new CreateRubricRequest(id, request.Criteria);
        var dto = await _svc.CreateRubricAsync(effective, createdByUserId, ct);
        return CreatedAtAction(nameof(GetActiveRubric), new { id }, dto);
    }

    /// <summary>
    /// Upload/replace the question paper file (PDF/PNG/JPG/DOCX, ≤20 MB).
    /// Replaces any previously stored file for this question.
    /// </summary>
    [HttpPost("{id:guid}/file")]
    [RequestSizeLimit(FileUploadValidator.MaxBytes)]
    public async Task<IActionResult> UploadFile(Guid id, IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0) return BadRequest(new { code = "EMPTY_FILE", message = "Empty file" });
        if (!FileUploadValidator.IsAllowedContentType(file.ContentType))
            return StatusCode(415, new { code = "UNSUPPORTED_MEDIA_TYPE", message = $"Unsupported file type '{file.ContentType}' — use PDF, PNG, JPG or DOCX" });

        var question = await _db.Questions.FindAsync([id], ct);
        if (question is null) return NotFound();

        // Delete the previous file to avoid orphaned blobs.
        if (!string.IsNullOrEmpty(question.FileStorageKey))
        {
            try { await _storage.DeleteAsync(question.FileStorageKey, ct); }
            catch (IOException ex) { _logger.LogWarning(ex, "Could not delete previous question file {Key}", question.FileStorageKey); }
        }

        var ext = FileUploadValidator.ExtensionFor(file.ContentType)!;
        var key = $"questions/{id:N}/{Guid.NewGuid():N}{ext}";
        await using var stream = file.OpenReadStream();
        await _storage.SaveAsync(stream, key, ct);

        question.FileStorageKey = key;
        question.FileName = Path.GetFileName(file.FileName); // strip any path segments
        question.ContentType = file.ContentType;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Question file uploaded {QuestionId} {FileName} ({Bytes} bytes)", id, question.FileName, file.Length);
        return Ok(new { fileStorageKey = key, fileName = question.FileName, contentType = question.ContentType });
    }

    /// <summary>Download/stream the stored question paper file.</summary>
    [HttpGet("{id:guid}/file")]
    public async Task<IActionResult> DownloadFile(Guid id, CancellationToken ct)
    {
        var question = await _db.Questions.FindAsync([id], ct);
        if (question is null || string.IsNullOrEmpty(question.FileStorageKey)) return NotFound();

        var stream = await _storage.OpenReadAsync(question.FileStorageKey, ct);
        if (stream is null) return NotFound();

        return File(stream, question.ContentType ?? "application/octet-stream",
            question.FileName ?? $"question-{id}");
    }

    /// <summary>Remove the stored question paper file.</summary>
    [HttpDelete("{id:guid}/file")]
    public async Task<IActionResult> DeleteFile(Guid id, CancellationToken ct)
    {
        var question = await _db.Questions.FindAsync([id], ct);
        if (question is null) return NotFound();
        if (string.IsNullOrEmpty(question.FileStorageKey)) return NoContent();

        try { await _storage.DeleteAsync(question.FileStorageKey, ct); }
        catch (IOException ex) { _logger.LogWarning(ex, "Could not delete question file {Key}", question.FileStorageKey); }

        question.FileStorageKey = null;
        question.FileName = null;
        question.ContentType = null;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Rubric document (attaches to the ACTIVE rubric version) ─────────────

    /// <summary>Upload/replace the rubric document for this question's active rubric.</summary>
    [HttpPost("{id:guid}/rubric/file")]
    [RequestSizeLimit(FileUploadValidator.MaxBytes)]
    public async Task<IActionResult> UploadRubricFile(Guid id, IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0) return BadRequest(new { code = "EMPTY_FILE", message = "Empty file" });
        if (!FileUploadValidator.IsAllowedContentType(file.ContentType))
            return StatusCode(415, new { code = "UNSUPPORTED_MEDIA_TYPE", message = $"Unsupported file type '{file.ContentType}' — use PDF, PNG, JPG or DOCX" });

        var rubric = await _db.Rubrics.FirstOrDefaultAsync(r => r.QuestionId == id && r.IsActive, ct);
        if (rubric is null) return NotFound(new { code = "NO_ACTIVE_RUBRIC", message = "Define a rubric before uploading its document" });

        if (!string.IsNullOrEmpty(rubric.FileStorageKey))
        {
            try { await _storage.DeleteAsync(rubric.FileStorageKey, ct); }
            catch (IOException ex) { _logger.LogWarning(ex, "Could not delete previous rubric file {Key}", rubric.FileStorageKey); }
        }

        var ext = FileUploadValidator.ExtensionFor(file.ContentType)!;
        var key = $"rubrics/{id:N}/{Guid.NewGuid():N}{ext}";
        await using var stream = file.OpenReadStream();
        await _storage.SaveAsync(stream, key, ct);

        rubric.FileStorageKey = key;
        rubric.FileName = Path.GetFileName(file.FileName);
        rubric.ContentType = file.ContentType;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Rubric file uploaded QuestionId={QuestionId} RubricId={RubricId} {FileName}", id, rubric.Id, rubric.FileName);
        return Ok(new { fileStorageKey = key, fileName = rubric.FileName, contentType = rubric.ContentType });
    }

    /// <summary>Download/stream the active rubric document.</summary>
    [HttpGet("{id:guid}/rubric/file")]
    public async Task<IActionResult> DownloadRubricFile(Guid id, CancellationToken ct)
    {
        var rubric = await _db.Rubrics.FirstOrDefaultAsync(r => r.QuestionId == id && r.IsActive, ct);
        if (rubric is null || string.IsNullOrEmpty(rubric.FileStorageKey)) return NotFound();

        var stream = await _storage.OpenReadAsync(rubric.FileStorageKey, ct);
        if (stream is null) return NotFound();

        return File(stream, rubric.ContentType ?? "application/octet-stream", rubric.FileName ?? $"rubric-{id}");
    }

    /// <summary>Remove the active rubric document.</summary>
    [HttpDelete("{id:guid}/rubric/file")]
    public async Task<IActionResult> DeleteRubricFile(Guid id, CancellationToken ct)
    {
        var rubric = await _db.Rubrics.FirstOrDefaultAsync(r => r.QuestionId == id && r.IsActive, ct);
        if (rubric is null) return NotFound();
        if (string.IsNullOrEmpty(rubric.FileStorageKey)) return NoContent();

        try { await _storage.DeleteAsync(rubric.FileStorageKey, ct); }
        catch (IOException ex) { _logger.LogWarning(ex, "Could not delete rubric file {Key}", rubric.FileStorageKey); }

        rubric.FileStorageKey = null;
        rubric.FileName = null;
        rubric.ContentType = null;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record UpdateQuestionRequest(string? Text, decimal? MaxScore, string? RubricText);