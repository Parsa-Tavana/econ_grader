using System.Security.Cryptography;
using EconGrader.Application.Data;
using EconGrader.Application.Interfaces;
using EconGrader.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EconGrader.Infrastructure.Storage;

public sealed class ArtifactStoreOptions
{
    /// <summary>Cap on pages rendered per ingest job (mirrors EXTRACT_MAX_PAGES
    /// on the Python side). When a document exceeds the cap, the excess pages
    /// are NOT rendered and grading falls back to the legacy whole-file path
    /// for that document.</summary>
    public int MaxPages { get; set; } = 20;
    /// <summary>Render DPI for the NEW ingest path (consuming render). The
    /// legacy grading path keeps its 200 DPI PNG; ingest always renders BOTH
    /// formats so the golden-set harness can A/B them (M4).</summary>
    public int RenderDpi { get; set; } = 150;
    public int JpegQuality { get; set; } = 85;
}

/// <summary>
/// Content-addressed artifact storage: blobs keyed by SHA-256 under the
/// existing storage root, with an <see cref="Artifact"/> row per blob/page.
///
/// Layout:
///   blobs/{sha[0:2]}/{sha}{ext}          — immutable uploaded originals
///   pages/{sha[0:2]}/{sha}{ext}          — rendered page images (hash of the
///                                          RENDERED page's own bytes)
///
/// Everything here is idempotent: saving the same bytes twice lands on the
/// same path and the same artifact row, so ingest can re-run safely.
/// The grading service reads the same paths — both containers share the
/// volume (exactly like the legacy LocalFileStorage keys).
/// </summary>
public sealed class ArtifactStore : IArtifactStore
{
    // Concrete DbContext (not IAppDbContext): the dedup race handler needs
    // ChangeTracker.Entry to detach a losing insert after the unique index
    // fires — an operation the app-level interface doesn't expose.
    private readonly AppDbContext _db;
    private readonly IFileStorage _storage;
    private readonly ILogger<ArtifactStore> _logger;

    public int MaxPages { get; }

    public ArtifactStore(AppDbContext db, IFileStorage storage, IOptions<ArtifactStoreOptions> options, ILogger<ArtifactStore> logger)
    {
        _db = db;
        _storage = storage;
        MaxPages = options.Value.MaxPages;
        _logger = logger;
    }

    public async Task<Artifact> SaveOriginalAsync(Stream content, string originalFileName, string contentType, CancellationToken ct = default)
    {
        // Single pass: hash while buffering to a temp file (uploads can be
        // 20 MB — never hold them fully in memory).
        var tempPath = Path.Combine(Path.GetTempPath(), "econgrader-upload-" + Guid.NewGuid().ToString("N") + ".tmp");
        string sha256;
        long bytes;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            await using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await content.CopyToAsync(temp, ct);
            }
            await using (var read = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                sha256 = await ComputeSha256Async(read, ct);
            bytes = new FileInfo(tempPath).Length;
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }

        var existing = await FindOriginalBySha256Async(sha256, ct);
        if (existing is not null)
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            _logger.LogInformation("Upload dedup hit: sha256={Sha} reuses artifact {ArtifactId}", sha256, existing.Id);
            return existing;
        }

        var ext = ExtensionForName(contentType, originalFileName);
        var key = $"blobs/{sha256[..2]}/{sha256}{ext}";
        await using (var read = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await _storage.SaveAsync(read, key, ct);
        }
        try { File.Delete(tempPath); } catch { /* best effort */ }

        var artifact = new Artifact
        {
            Kind = "original",
            StorageKey = key,
            Sha256 = sha256,
            Bytes = bytes,
        };
        _db.Artifacts.Add(artifact);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent identical upload inserted the same (Sha256, Kind) row
            // first — the unique index fired. The file write itself is idempotent
            // (same bytes, same path). Re-query and return the winner.
            _db.Entry(artifact).State = EntityState.Detached;
            existing = await FindOriginalBySha256Async(sha256, ct);
            if (existing is null) throw;
            return existing;
        }
        _logger.LogInformation("Stored original artifact {ArtifactId} {Key} ({Bytes} bytes)", artifact.Id, key, bytes);
        return artifact;
    }

    public async Task<Artifact> RegisterOriginalAsync(string legacyKey, CancellationToken ct = default)
    {
        await using var stream = await _storage.OpenReadAsync(legacyKey, ct)
            ?? throw new FileNotFoundException($"Legacy file not found: {legacyKey}");
        var sha256 = await ComputeSha256Async(stream, ct);
        var bytes = stream.Length;

        var existing = await FindOriginalBySha256Async(sha256, ct);
        if (existing is not null) return existing;

        // Backfill: keep the file where it is; the artifact row points at the
        // legacy key. Content addressing only applies to NEW uploads.
        var artifact = new Artifact
        {
            Kind = "original",
            StorageKey = legacyKey,
            Sha256 = sha256,
            Bytes = bytes,
        };
        _db.Artifacts.Add(artifact);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.Entry(artifact).State = EntityState.Detached;
            existing = await FindOriginalBySha256Async(sha256, ct);
            if (existing is null) throw;
            return existing;
        }
        return existing ?? artifact;
    }

    public async Task<Artifact> RegisterPageAsync(
        byte[] pageBytes, string extension, int? width, int? height,
        Guid parentArtifactId, string? format = null, CancellationToken ct = default)
    {
        var sha256 = Convert.ToHexString(SHA256.HashData(pageBytes)).ToLowerInvariant();
        var key = $"pages/{sha256[..2]}/{sha256}{extension}";

        // Dedup FIRST (no file write, no row insert when the bytes exist).
        var existing = await _db.Artifacts.FirstOrDefaultAsync(a =>
            a.Sha256 == sha256 && a.Kind == "page", ct);
        if (existing is not null)
        {
            _logger.LogDebug("Page dedup hit: sha256={Sha} (parent {Parent})", sha256, parentArtifactId);
            return existing;
        }

        await using var stream = new MemoryStream(pageBytes);
        await _storage.SaveAsync(stream, key, ct);

        var artifact = new Artifact
        {
            Kind = "page",
            StorageKey = key,
            Sha256 = sha256,
            Bytes = pageBytes.LongLength,
            Width = width,
            Height = height,
            Format = format,
            ParentArtifactId = parentArtifactId,
        };
        _db.Artifacts.Add(artifact);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Concurrent render of the same page — the row exists now.
            _db.Entry(artifact).State = EntityState.Detached;
            existing = await _db.Artifacts.FirstOrDefaultAsync(a => a.Sha256 == sha256 && a.Kind == "page", ct);
            if (existing is null) throw;
            return existing;
        }
        return artifact;
    }

    public Task<Artifact?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, ct);

    public Task<Artifact?> FindOriginalBySha256Async(string sha256, CancellationToken ct = default) =>
        _db.Artifacts.FirstOrDefaultAsync(a => a.Sha256 == sha256 && a.Kind == "original", ct);

    public string AbsolutePath(string storageKey) => _storage.GetAbsolutePath(storageKey);
    public bool Exists(string storageKey) => _storage.Exists(storageKey);
    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default) => _storage.OpenReadAsync(storageKey, ct);
    public Task DeleteAsync(string storageKey, CancellationToken ct = default) => _storage.DeleteAsync(storageKey, ct);

    private static async Task<string> ComputeSha256Async(Stream stream, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        stream.Seek(0, SeekOrigin.Begin);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ExtensionForName(string contentType, string originalFileName)
    {
        // Canonical extension resolution mirrors FileUploadValidator; the
        // store never mutates content, so the extension is cosmetic.
        var ext = Path.GetExtension(originalFileName ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".jpeg" => ".jpg",
            ".png" or ".jpg" or ".pdf" or ".docx" or ".xlsx" or ".xls" => ext,
            _ => contentType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "application/pdf" => ".pdf",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
                "application/vnd.ms-excel" => ".xls",
                _ => ".bin",
            },
        };
    }
}
