using EconGrader.Domain.Entities;

namespace EconGrader.Application.Interfaces;

/// <summary>
/// Content-addressed artifact storage abstraction: blobs keyed by SHA-256,
/// with an Artifact row per blob/page. Implemented over IFileStorage.
/// </summary>
public interface IArtifactStore
{
    /// <summary>Stream the upload into the store, hashing as it goes.
    /// Creates (or returns the existing) original artifact row.</summary>
    Task<Artifact> SaveOriginalAsync(Stream content, string originalFileName, string contentType, CancellationToken ct = default);

    /// <summary>Register an EXISTING file (legacy key) as an original artifact
    /// without moving it — the backfill path; bytes stay where they are.</summary>
    Task<Artifact> RegisterOriginalAsync(string legacyKey, CancellationToken ct = default);

    /// <summary>Register a rendered page produced by the ingest pipeline.
    /// Dedups by (sha256, kind=page) — identical rendered pages across
    /// documents share one row + one file. format records which render
    /// produced the page ("png200"/"jpeg150") so resolvers can prefer one.</summary>
    Task<Artifact> RegisterPageAsync(
        byte[] pageBytes, string extension, int? width, int? height,
        Guid parentArtifactId, string? format = null, CancellationToken ct = default);

    /// <summary>Configured page cap for ingest renders (excess pages are not
    /// rendered; grading falls back to the legacy whole-file path).</summary>
    int MaxPages { get; }

    Task<Artifact?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Artifact?> FindOriginalBySha256Async(string sha256, CancellationToken ct = default);

    /// <summary>Absolute path for a storage key (for the Python service).</summary>
    string AbsolutePath(string storageKey);
    bool Exists(string storageKey);
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default);
    Task DeleteAsync(string storageKey, CancellationToken ct = default);
}
