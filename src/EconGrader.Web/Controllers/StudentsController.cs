using Microsoft.AspNetCore.Mvc;
using EconGrader.Application.DTOs;
using EconGrader.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class StudentsController : ControllerBase
{
    private readonly IAppDbContext _db;

    public StudentsController(IAppDbContext db) => _db = db;

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var s = await _db.Students.FindAsync([id], ct);
        return s is null ? NotFound() : Ok(new StudentDto(s.Id, s.ExternalId, s.DisplayName, s.CreatedAt));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await _db.Students
            .OrderBy(s => s.ExternalId)
            .Select(s => new StudentDto(s.Id, s.ExternalId, s.DisplayName, s.CreatedAt))
            .ToListAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateStudentRequest request, CancellationToken ct)
    {
        if (await _db.Students.AnyAsync(s => s.ExternalId == request.ExternalId, ct))
            return Conflict($"A student with ExternalId '{request.ExternalId}' already exists");

        var student = new EconGrader.Domain.Entities.Student
        {
            ExternalId = request.ExternalId,
            DisplayName = request.DisplayName,
        };
        _db.Students.Add(student);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = student.Id },
            new StudentDto(student.Id, student.ExternalId, student.DisplayName, student.CreatedAt));
    }
}
