using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EconGrader.Application.Interfaces;
using EconGrader.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace EconGrader.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = nameof(UserRole.Admin))]
public sealed class AuditController : ControllerBase
{
    private readonly IAuditLogger _audit;

    public AuditController(IAuditLogger audit) => _audit = audit;

    public record AuditEntryDto(
        Guid Id,
        DateTime Timestamp,
        string Action,
        string EntityType,
        string? EntityId,
        Guid? UserId,
        string? Details,
        string? IpAddress);

    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] Guid? entityId,
        [FromQuery] string? entityType,
        [FromQuery] Guid? userId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var logs = await _audit.QueryAsync(entityId, entityType, userId, from, to, skip, take, ct);
        return Ok(logs.Select(l => new AuditEntryDto(l.Id, l.Timestamp, l.Action, l.EntityType, l.EntityId, l.UserId, l.Details, l.IpAddress)));
    }

    /// <summary>Manual entry write — attributed to the authenticated admin.</summary>
    [HttpPost]
    public async Task<IActionResult> Write(
        [FromBody] WriteAuditRequest body,
        CancellationToken ct)
    {
        Guid? userId = Guid.TryParse(User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier), out var id) ? id : null;
        await _audit.WriteAsync(body.Action, body.EntityType, body.EntityId, userId, body.Details, body.IpAddress, ct);
        return Accepted();
    }
}

public record WriteAuditRequest(
    string Action,
    string EntityType,
    Guid? EntityId,
    object? Details = null,
    string? IpAddress = null);
