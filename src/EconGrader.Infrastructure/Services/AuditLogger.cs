using EconGrader.Application.Interfaces;
using EconGrader.Application.Data;
using EconGrader.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace EconGrader.Infrastructure.Services;

/// <summary>EF Core–backed audit trail — append-only, never edited/deleted.</summary>
public sealed class AuditLogger : IAuditLogger
{
    private readonly AppDbContext _db;
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(AppDbContext db, ILogger<AuditLogger> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task WriteAsync(
        string action,
        string entityType,
        Guid? entityId,
        Guid? userId,
        object? details = null,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        var log = new AuditLog
        {
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            UserId = userId,
            Details = details != null ? JsonSerializer.Serialize(details) : null,
            IpAddress = ipAddress,
        };
        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "AUDIT {Action} {EntityType} {EntityId} by {UserId} from {Ip}",
            action, entityType, entityId, userId, ipAddress);
    }

    public async Task<IReadOnlyList<AuditLog>> QueryAsync(
        Guid? entityId = null,
        string? entityType = null,
        Guid? userId = null,
        DateTime? from = null,
        DateTime? to = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default)
    {
        var query = _db.AuditLogs.AsQueryable();
        if (entityId.HasValue) query = query.Where(l => l.EntityId == entityId.Value.ToString());
        if (!string.IsNullOrEmpty(entityType)) query = query.Where(l => l.EntityType == entityType);
        if (userId.HasValue) query = query.Where(l => l.UserId == userId.Value);
        if (from.HasValue) query = query.Where(l => l.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(l => l.Timestamp <= to.Value);

        return await query
            .OrderByDescending(l => l.Timestamp)
            .Skip(skip).Take(take)
            .ToListAsync(ct);
    }
}