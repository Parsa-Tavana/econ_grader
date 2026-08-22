using EconGrader.Domain.Entities;

namespace EconGrader.Application.Interfaces;

/// <summary>Object storage abstraction (local disk now, S3-compatible later).</summary>
public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, string key, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
    string GetAbsolutePath(string key);
    bool Exists(string key);
}

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