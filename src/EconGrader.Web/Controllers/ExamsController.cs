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

    public ExamsController(IExamService svc, IAccessScopeService scope, CurrentUser user, IAppDbContext db)
    {
        _svc = svc;
        _scope = scope;
        _user = user;
        _db = db;
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