using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EconGrader.Application.DTOs;
using EconGrader.Application.Interfaces;
using EconGrader.Application.Services;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AnswersController : ControllerBase
{
    private readonly IAnswerService _svc;
    private readonly IFileStorage _storage;
    private readonly IAppDbContext _db;
    private readonly ILogger<AnswersController> _logger;

    public AnswersController(IAnswerService svc, IFileStorage storage, IAppDbContext db, ILogger<AnswersController> logger)
    {
        _svc = svc;
        _storage = storage;
        _db = db;
        _logger = logger;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AnswerDto>> Get(Guid id, CancellationToken ct) =>
        await _svc.GetAsync(id, ct) is { } dto ? Ok(dto) : NotFound();

    [HttpGet("by-question/{questionId:guid}")]
    public async Task<ActionResult<IReadOnlyList<AnswerDto>>> ListByQuestion(Guid questionId, CancellationToken ct) =>
        Ok(await _svc.ListByQuestionAsync(questionId, ct));

    /// <summary>
    /// Upload a scanned answer sheet (PNG/JPG) for one student + one question.
    /// The file is stored via IFileStorage; the answer row is created with an
    /// optional teacher ground-truth score (never exposed to the AI).
    /// </summary>
    /// <summary>
    /// Upload a scanned/typed answer sheet (PNG/JPG/PDF/DOCX/XLSX/XLS, ≤20 MB) for one
    /// student + one question. Stored via IFileStorage; optional teacher
    /// ground-truth scores are never exposed to the AI.
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(FileUploadValidator.MaxBytes)]
    public async Task<ActionResult<AnswerDto>> Upload(
        [FromForm] Guid studentId,
        [FromForm] Guid questionId,
        [FromForm] decimal? teacherScore,
        [FromForm] decimal? teacher2Score,
        IFormFile file,
        CancellationToken ct)
    {
        if (file.Length == 0) return BadRequest(new { code = "EMPTY_FILE", message = "Empty file" });
        var ext = FileUploadValidator.ExtensionForFile(file.ContentType, file.FileName);
        if (ext is null)
            return StatusCode(415, new { code = "UNSUPPORTED_MEDIA_TYPE", message = $"Unsupported file type '{file.ContentType}' — use {FileUploadValidator.AcceptedTypesDisplay}" });

        // Ensure the student exists
        if (await _db.Students.FindAsync([studentId], ct) is null)
            return BadRequest(new { code = "UNKNOWN_STUDENT", message = $"Unknown student {studentId}" });

        // One answer per (student, question): replace the previous file.
        var existing = await _db.Answers.FirstOrDefaultAsync(a => a.StudentId == studentId && a.QuestionId == questionId, ct);
        if (existing is not null && !string.IsNullOrEmpty(existing.ImageStorageKey))
        {
            try { await _storage.DeleteAsync(existing.ImageStorageKey, ct); }
            catch (IOException ex) { _logger.LogWarning(ex, "Could not delete previous answer file {Key}", existing.ImageStorageKey); }
        }

        var key = $"answers/{questionId:N}/{studentId:N}/{Guid.NewGuid():N}{ext}";
        await using var stream = file.OpenReadStream();
        await _storage.SaveAsync(stream, key, ct);

        if (existing is not null)
        {
            existing.ImageStorageKey = key;
            existing.FileName = Path.GetFileName(file.FileName);
            existing.ContentType = file.ContentType;
            existing.UploadedAt = DateTime.UtcNow;
            if (teacherScore.HasValue) existing.TeacherScore = teacherScore.Value;
            if (teacher2Score.HasValue) existing.Teacher2Score = teacher2Score.Value;
            await _db.SaveChangesAsync(ct);
            return Ok(await _svc.GetAsync(existing.Id, ct));
        }

        var dto = await _svc.CreateAsync(new CreateAnswerRequest(
            StudentId: studentId,
            QuestionId: questionId,
            ImageStorageKey: key,
            TeacherScore: teacherScore,
            Teacher2Score: teacher2Score), ct);

        // Persist display metadata alongside the blob.
        var entity = await _db.Answers.FirstAsync(a => a.Id == dto.Id, ct);
        entity.FileName = Path.GetFileName(file.FileName);
        entity.ContentType = file.ContentType;
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>
    /// Retrieve the stored answer file for display/download in review UI.
    /// Serves images inline; PDFs inline via browser viewer; DOCX as download.
    /// </summary>
    [HttpGet("{id:guid}/image")]
    public async Task<IActionResult> GetImage(Guid id, CancellationToken ct)
    {
        var dto = await _svc.GetAsync(id, ct);
        if (dto is null) return NotFound();

        var stream = await _storage.OpenReadAsync(dto.ImageStorageKey, ct);
        if (stream is null) return NotFound();

        // Prefer the recorded content type; fall back to extension sniffing
        // for legacy rows created before ContentType existed.
        var contentType = dto.ContentType;
        if (string.IsNullOrEmpty(contentType))
        {
            var key = dto.ImageStorageKey.ToLowerInvariant();
            if (key.EndsWith(".png")) contentType = "image/png";
            else if (key.EndsWith(".jpg") || key.EndsWith(".jpeg")) contentType = "image/jpeg";
            else if (key.EndsWith(".pdf")) contentType = "application/pdf";
            else if (key.EndsWith(".docx"))
                contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            else contentType = "application/octet-stream";
        }

        var inline = contentType.StartsWith("image/") || contentType == "application/pdf";
        return File(stream, contentType,
            inline ? null : dto.FileName ?? $"answer-{id}");
    }

    /// <summary>Set/adjust the teacher ground-truth score after AI grading.</summary>
    [HttpPut("{id:guid}/teacher-score")]
    public async Task<ActionResult<AnswerDto>> SetTeacherScore(
        Guid id,
        [FromBody] SetTeacherScoreRequest request,
        CancellationToken ct) =>
        Ok(await _svc.SetTeacherScoreAsync(id, request.Score, request.Teacher2Score, ct));
}

public record SetTeacherScoreRequest(decimal Score, decimal? Teacher2Score);