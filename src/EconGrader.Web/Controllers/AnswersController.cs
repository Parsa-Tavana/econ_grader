using Microsoft.AspNetCore.Mvc;
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

    public AnswersController(IAnswerService svc, IFileStorage storage, IAppDbContext db)
    {
        _svc = svc;
        _storage = storage;
        _db = db;
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
    [HttpPost("upload")]
    [RequestSizeLimit(20_000_000)] // 20 MB per image
    public async Task<ActionResult<AnswerDto>> Upload(
        [FromForm] Guid studentId,
        [FromForm] Guid questionId,
        [FromForm] decimal? teacherScore,
        [FromForm] decimal? teacher2Score,
        IFormFile file,
        CancellationToken ct)
    {
        if (file.Length == 0) return BadRequest("Empty file");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg"))
            return BadRequest($"Unsupported extension '{ext}' — use .png/.jpg/.jpeg");

        // Ensure the student exists
        if (await _db.Students.FindAsync([studentId], ct) is null)
            return BadRequest($"Unknown student {studentId}");

        var key = $"answers/{questionId:N}/{studentId:N}/{Guid.NewGuid():N}{ext}";
        await using var stream = file.OpenReadStream();
        await _storage.SaveAsync(stream, key, ct);

        var dto = await _svc.CreateAsync(new CreateAnswerRequest(
            StudentId: studentId,
            QuestionId: questionId,
            ImageStorageKey: key,
            TeacherScore: teacherScore,
            Teacher2Score: teacher2Score), ct);

        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Retrieve the scanned answer image for display in review UI.</summary>
    [HttpGet("{id:guid}/image")]
    public async Task<IActionResult> GetImage(Guid id, CancellationToken ct)
    {
        var dto = await _svc.GetAsync(id, ct);
        if (dto is null) return NotFound();

        var stream = await _storage.OpenReadAsync(dto.ImageStorageKey, ct);
        if (stream is null) return NotFound();

        var contentType = dto.ImageStorageKey.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png" : "image/jpeg";
        return File(stream, contentType);
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