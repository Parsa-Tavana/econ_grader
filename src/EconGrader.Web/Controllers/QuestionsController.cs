using Microsoft.AspNetCore.Mvc;
using EconGrader.Application.DTOs;
using EconGrader.Application.Services;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class QuestionsController : ControllerBase
{
    private readonly IQuestionService _svc;

    public QuestionsController(IQuestionService svc) => _svc = svc;

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
}

public record UpdateQuestionRequest(string? Text, decimal? MaxScore, string? RubricText);