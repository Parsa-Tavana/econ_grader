using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using EconGrader.Application.Interfaces;

namespace EconGrader.Infrastructure.Storage;

public sealed class LocalFileStorageOptions
{
    /// <summary>Absolute or relative root where uploaded files are stored.</summary>
    public string RootPath { get; set; } = "storage/images";
}

/// <summary>
/// Local-disk implementation of IFileStorage. Keys are relative paths
/// (e.g. "answers/{questionId}/{studentId}/{file}.png"); files are written
/// under RootPath. The grading service reads these same paths, so in Docker
/// both containers must mount the SAME volume at the SAME path.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _root;
    private readonly ILogger<LocalFileStorage> _logger;

    public LocalFileStorage(IOptions<LocalFileStorageOptions> options, ILogger<LocalFileStorage> logger)
    {
        _root = Path.GetFullPath(options.Value.RootPath);
        _logger = logger;
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(Stream content, string key, CancellationToken cancellationToken = default)
    {
        var fullPath = FullPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(fs, cancellationToken);

        _logger.LogInformation("Saved file {Key} ({Bytes} bytes)", key, fs.Length);
        return key;
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var fullPath = FullPath(key);
        if (!File.Exists(fullPath)) return Task.FromResult<Stream?>(null);
        return Task.FromResult<Stream?>(new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var fullPath = FullPath(key);
        if (File.Exists(fullPath)) File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public string GetAbsolutePath(string key) => FullPath(key);

    public bool Exists(string key) => File.Exists(FullPath(key));

    /// <summary>
    /// Resolves a key under the storage root, rejecting path traversal
    /// (keys must stay inside RootPath).
    /// </summary>
    private string FullPath(string key)
    {
        // Normalize separators and strip any leading slashes.
        var normalized = key.Replace('\\', '/').TrimStart('/');
        var combined = Path.GetFullPath(Path.Combine(_root, normalized));
        if (!combined.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Invalid storage key '{key}' — escapes the storage root.");
        return combined;
    }
}