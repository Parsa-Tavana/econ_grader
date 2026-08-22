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