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
public sealed class AnswersController : ControllerBase
{
    private readonly IAnswerService _svc;
    private readonly IFileStorage _storage;
    private readonly IAppDbContext _db;
    private readonly ILogger<AnswersController> _logger;
    private readonly CurrentUser _user;
    private readonly IAccessScopeService _scope;

    public AnswersController(IAnswerService svc, IFileStorage storage, IAppDbContext db,
        ILogger<AnswersController> logger, CurrentUser user, IAccessScopeService scope)
    {
        _svc = svc;
        _storage = storage;
        _db = db;
        _logger = logger;
        _user = user;
        _scope = scope;
    }

    /// <summary>
    /// Answer detail. Students may fetch only their own answer; the DTO is
    /// filtered so they never see teacher ground-truth scores.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AnswerDto>> Get(Guid id, CancellationToken ct)
    {
        if (!await _scope.CanAccessAnswerAsync(_user, id, writeAccess: false, ct)) return Forbid();
        var dto = await _svc.GetAsync(id, ct);
        if (dto is null) return NotFound();
        return Ok(_user.IsStudent ? StudentView(dto) : dto);
    }

    /// <summary>All answers for a question. Correctors read-only on assigned
    /// exams; students get only their own answer (if any), scores hidden.</summary>
    [HttpGet("by-question/{questionId:guid}")]
    public async Task<ActionResult<IReadOnlyList<AnswerDto>>> ListByQuestion(Guid questionId, CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, questionId, writeAccess: false, ct)) return Forbid();
        var list = await _svc.ListByQuestionAsync(questionId, ct);

        if (_user.IsStudent)
        {
            // Resolve the caller's Student row; hide teacher scores on it.
            var studentId = (await _db.Students.FirstOrDefaultAsync(s => s.UserId == _user.UserId, ct))?.Id;
            return Ok(list.Where(a => a.StudentId == studentId).Select(StudentView).ToList());
        }
        return Ok(list);
    }

    /// <summary>
    /// Upload a scanned/typed answer sheet (PNG/JPG/PDF/DOCX/XLSX/XLS, ≤20 MB) for one
    /// student + one question. Stored via IFileStorage; optional teacher
    /// ground-truth scores are never exposed to the AI. Teachers only.
    /// </summary>
    [HttpPost("upload")]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    [RequestSizeLimit(FileUploadValidator.MaxBytes)]
    public async Task<ActionResult<AnswerDto>> Upload(
        [FromForm] Guid studentId,
        [FromForm] Guid questionId,
        [FromForm] decimal? teacherScore,
        [FromForm] decimal? teacher2Score,
        IFormFile file,
        CancellationToken ct)
    {
        if (!await _scope.CanAccessQuestionAsync(_user, questionId, writeAccess: true, ct))
            return Forbid();
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
        if (!await _scope.CanAccessAnswerAsync(_user, id, writeAccess: false, ct)) return Forbid();

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

    /// <summary>
    /// Set ground-truth score. Teachers set TeacherScore (+ optionally
    /// Teacher2Score). Correctors may ONLY set Teacher2Score — pass a body with
    /// teacher2Score and score omitted/null; attempts to touch TeacherScore are rejected.
    /// </summary>
    [HttpPut("{id:guid}/teacher-score")]
    public async Task<ActionResult<AnswerDto>> SetTeacherScore(
        Guid id,
        [FromBody] SetTeacherScoreRequest request,
        CancellationToken ct)
    {
        if (_user.IsTeacher || _user.IsAdmin)
        {
            if (!request.Score.HasValue)
                return BadRequest(new { code = "SCORE_REQUIRED", message = "Teachers must provide 'score'" });
            if (!await _scope.CanAccessAnswerAsync(_user, id, writeAccess: true, ct)) return Forbid();
            return Ok(await _svc.SetTeacherScoreAsync(id, request.Score.Value, request.Teacher2Score, ct));
        }

        if (_user.IsCorrector)
        {
            // Independence by construction: a corrector never writes the first rater's score.
            if (request.Score.HasValue)
                return StatusCode(403, new { code = "CORRECTOR_SCORE_FORBIDDEN", message = "Correctors may only set teacher2Score — 'score' must be null" });
            if (!request.Teacher2Score.HasValue)
                return BadRequest(new { code = "EMPTY_UPDATE", message = "Provide teacher2Score to update" });
            if (!await _scope.CanAccessAnswerAsync(_user, id, writeAccess: false, ct)) return Forbid();

            var answer = await _db.Answers.FindAsync([id], ct)
                ?? throw new KeyNotFoundException($"Answer {id} not found");
            if (request.Teacher2Score.Value < 0)
                return BadRequest(new { code = "INVALID_SCORE", message = "Teacher2Score cannot be negative" });
            answer.Teacher2Score = request.Teacher2Score.Value;
            await _db.SaveChangesAsync(ct);
            return Ok(await _svc.GetAsync(id, ct));
        }

        return Forbid(); // Students (and any future role) never set scores.
    }

    /// <summary>Student-safe projection: teacher ground-truth scores stripped.</summary>
    private static AnswerDto StudentView(AnswerDto a) => a with
    {
        TeacherScore = null,
        Teacher2Score = null,
        GradingRuns = a.GradingRuns.Select(r => r with { TeacherScoreSnapshot = null }).ToList(),
    };
}

public record SetTeacherScoreRequest(decimal? Score, decimal? Teacher2Score);
