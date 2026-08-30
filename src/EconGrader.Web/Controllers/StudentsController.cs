using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EconGrader.Application.DTOs;
using EconGrader.Application.Interfaces;
using EconGrader.Domain.Entities;
using EconGrader.Web.Services;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class StudentsController : ControllerBase
{
    private readonly IAppDbContext _db;
    private readonly CurrentUser _user;

    public StudentsController(IAppDbContext db, CurrentUser user)
    {
        _db = db;
        _user = user;
    }

    /// <summary>Single student. Teachers/admins unrestricted; students only themselves.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (_user.IsStudent)
        {
            var own = await _db.Students.AnyAsync(s => s.Id == id && s.UserId == _user.UserId, ct);
            if (!own) return Forbid();
        }
        var s = await _db.Students.FindAsync([id], ct);
        return s is null ? NotFound() : Ok(new StudentDto(s.Id, s.ExternalId, s.DisplayName, s.CreatedAt));
    }

    /// <summary>Roster. Students get only their own record.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (_user.IsStudent)
        {
            var me = await _db.Students
                .Where(s => s.UserId == _user.UserId)
                .Select(s => new StudentDto(s.Id, s.ExternalId, s.DisplayName, s.CreatedAt))
                .ToListAsync(ct);
            return Ok(me);
        }
        return Ok(await _db.Students
            .OrderBy(s => s.ExternalId)
            .Select(s => new StudentDto(s.Id, s.ExternalId, s.DisplayName, s.CreatedAt))
            .ToListAsync(ct));
    }

    /// <summary>Register a student; optionally link a login account (Role=Student).</summary>
    [HttpPost]
    [Authorize(Roles = nameof(UserRole.Teacher))]
    public async Task<IActionResult> Create([FromBody] CreateStudentRequest request, CancellationToken ct)
    {
        if (await _db.Students.AnyAsync(s => s.ExternalId == request.ExternalId, ct))
            return Conflict($"A student with ExternalId '{request.ExternalId}' already exists");

        var student = new Student
        {
            ExternalId = request.ExternalId,
            DisplayName = request.DisplayName,
        };
        _db.Students.Add(student);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = student.Id },
            new StudentDto(student.Id, student.ExternalId, student.DisplayName, student.CreatedAt));
    }

    /// <summary>
    /// Link or unlink a login account to a student row (Admin/Teacher).
    /// Body: { "userId": "guid" } to link an existing Role=Student user, or
    /// { "userId": null } to unlink.
    /// </summary>
    [HttpPut("{id:guid}/link-user")]
    [Authorize(Roles = $"{nameof(UserRole.Admin)},{nameof(UserRole.Teacher)}")]
    public async Task<IActionResult> LinkUser(Guid id, [FromBody] LinkStudentUserRequest body, CancellationToken ct)
    {
        var student = await _db.Students.FindAsync([id], ct);
        if (student is null) return NotFound();

        if (body.UserId.HasValue)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == body.UserId.Value, ct);
            if (user is null) return NotFound(new { code = "USER_NOT_FOUND", message = $"No user {body.UserId}" });
            if (user.Role != UserRole.Student)
                return BadRequest(new { code = "NOT_A_STUDENT_USER", message = "Only accounts with Role=Student can be linked" });

            // Enforce the 1:1 mapping from both directions.
            var alreadyLinked = await _db.Students.AnyAsync(s => s.UserId == user.Id && s.Id != id, ct);
            if (alreadyLinked)
                return Conflict(new { code = "USER_ALREADY_LINKED", message = "That account is linked to another student" });
            student.UserId = user.Id;
        }
        else
        {
            student.UserId = null;
        }

        await _db.SaveChangesAsync(ct);
        var s = await _db.Students.FindAsync([id], ct);
        return Ok(new StudentDto(s!.Id, s.ExternalId, s.DisplayName, s.CreatedAt));
    }
}

public record LinkStudentUserRequest(Guid? UserId);
