namespace EconGrader.Application.Interfaces;

/// <summary>Audit trail — append-only.</summary>
public interface IAuditLogger
{
    Task WriteAsync(
        string action,
        string entityType,
        Guid? entityId,
        Guid? userId,
        object? details = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<AuditLog>> QueryAsync(
        Guid? entityId = null,
        string? entityType = null,
        Guid? userId = null,
        DateTime? from = null,
        DateTime? to = null,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default
    );
}