using Microsoft.AspNetCore.Mvc;
using EconGrader.Application.DTOs;
using EconGrader.Application.Interfaces;
using EconGrader.Application.Services;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ExamsController : ControllerBase
{
    private readonly IExamService _svc;

    public ExamsController(IExamService svc) => _svc = svc;

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ExamDto>> Get(Guid id, CancellationToken ct) =>
        await _svc.GetAsync(id, ct) is { } dto ? Ok(dto) : NotFound();

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ExamDto>>> List(CancellationToken ct) =>
        Ok(await _svc.ListAsync(ct));

    [HttpPost]
    public async Task<ActionResult<ExamDto>> Create(
        [FromBody] CreateExamRequest request,
        [FromHeader(Name = "X-User-Id")] Guid createdByUserId,
        CancellationToken ct)
    {
        var dto = await _svc.CreateAsync(request, createdByUserId, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ExamDto>> Update(
        Guid id,
        [FromBody] UpdateExamRequest request,
        CancellationToken ct) =>
        await _svc.UpdateAsync(id, request, ct) is { } dto ? Ok(dto) : NotFound();

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await _svc.DeleteAsync(id, ct) ? NoContent() : NotFound();
}