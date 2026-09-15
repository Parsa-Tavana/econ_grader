using System.Text.Json;
using EconGrader.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EconGrader.Application.Services;

/// <summary>Result of one ingest job execution.</summary>
public sealed record IngestResult(bool Completed, string? Error);

/// <summary>
/// The M1 ingest pipeline (.NET side): takes an original artifact (an upload
/// blob) and materializes it into page artifacts + extracted text via the
/// Python /ingest endpoint — ONCE per document, never per grading request.
///
/// Idempotent by construction: page registration dedups on rendered content
/// hash, so re-running an ingest job re-uses every artifact that already
/// exists ("same input hash → skips re-render").
///
/// All rendered bytes flow through memory here (one page at a time, ~200 KB
/// JPEG / ~2 MB PNG); payloads are never persisted to temp files.
/// </summary>
public interface IIngestService
{
    /// <summary>Enqueue an ingest job for an original artifact (deduped: an
    /// existing pending/running/completed job for the same artifact returns it
    /// instead of creating a new one).</summary>
    Task<IngestJob> EnqueueAsync(Guid originalArtifactId, string role, CancellationToken ct = default);

    /// <summary>Process an ingest job: claim (lease), render, register pages,
    /// complete. Called by the worker loop (M2) or inline after upload (M1).</summary>
    Task<IngestResult> RunJobAsync(Guid ingestJobId, string leaseToken, CancellationToken ct = default);

    /// <summary>Claim the next pending ingest job (lease-based, safe for
    /// multiple workers). Returns null when nothing is claimable.</summary>
    Task<IngestJob?> ClaimNextAsync(TimeSpan leaseDuration, CancellationToken ct = default);

    /// <summary>Run/complete ingest for an artifact synchronously if it hasn't
    /// run yet (used by grading fallback paths when artifacts are missing).</summary>
    Task<bool> EnsureIngestedAsync(Guid originalArtifactId, string role, CancellationToken ct = default);
}

public sealed class IngestOptions
{
    /// <summary>Lease duration for claimed ingest jobs.</summary>
    public int LeaseSeconds { get; set; } = 300;

    /// <summary>Page format grading prefers when both exist ("png200" is the
    /// legacy-safe default; Ingest:PageFormat flips to jpeg150 after the M4
    /// golden-set A/B shows equivalent accuracy).</summary>
    public string PageFormat { get; set; } = "png200";
}

public sealed class IngestService : IIngestService
{
    private readonly IAppDbContext _db;
    private readonly IGradingClient _gradingClient;
    private readonly IArtifactStore _artifacts;
    private readonly IOptions<IngestOptions> _options;
    private readonly ILogger<IngestService> _logger;

    public IngestService(
        IAppDbContext db,
        IGradingClient gradingClient,
        IArtifactStore artifacts,
        IOptions<IngestOptions> options,
        ILogger<IngestService> logger)
    {
        _db = db;
        _gradingClient = gradingClient;
        _artifacts = artifacts;
        _options = options;
        _logger = logger;
    }

    private int MaxPages => _artifacts.MaxPages;

    public async Task<IngestJob> EnqueueAsync(Guid originalArtifactId, string role, CancellationToken ct = default)
    {
        var existing = await _db.IngestJobs
            .FirstOrDefaultAsync(j => j.OriginalArtifactId == originalArtifactId && j.Status != "failed", ct);
        if (existing is not null)
        {
            _logger.LogDebug("Ingest job {JobId} already {Status} for artifact {ArtifactId}",
                existing.Id, existing.Status, originalArtifactId);
            return existing;
        }

        var job = new IngestJob
        {
            OriginalArtifactId = originalArtifactId,
            Role = role,
        };
        _db.IngestJobs.Add(job);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Enqueued ingest job {JobId} artifact {ArtifactId} role {Role}", job.Id, originalArtifactId, role);
        return job;
    }

    public async Task<IngestJob?> ClaimNextAsync(TimeSpan leaseDuration, CancellationToken ct = default)
    {
        // Lease-claim without READPAST (works on SQL Server + SQLite/HANA in dev):
        // find claimable jobs, claim the first whose lease is free/expired, and
        // rely on the LeasedToken concurrency check to fail a second claimer.
        var candidates = await _db.IngestJobs
            .Where(j => j.Status == "pending" || (j.Status == "running" && (j.LeaseUntil == null || j.LeaseUntil < DateTime.UtcNow)))
            .OrderBy(j => j.CreatedAt)
            .Take(5)
            .ToListAsync(ct);

        foreach (var job in candidates)
        {
            var token = Guid.NewGuid().ToString("N");
            var claim = await _db.IngestJobs
                .Where(j => j.Id == job.Id &&
                            j.Status == "pending" &&
                            (j.LeaseUntil == null || j.LeaseUntil < DateTime.UtcNow))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, "running")
                    .SetProperty(j => j.LeaseToken, token)
                    .SetProperty(j => j.LeaseUntil, DateTime.UtcNow.Add(leaseDuration))
                    .SetProperty(j => j.StartedAt, DateTime.UtcNow)
                    .SetProperty(j => j.Attempts, j => j.Attempts + 1), ct);
            if (claim == 1)
            {
                var claimed = await _db.IngestJobs.FirstAsync(j => j.Id == job.Id, ct);
                claimed.LeaseToken = token; // local knowledge of the new token
                return claimed;
            }
        }
        return null;
    }

    public async Task<IngestResult> RunJobAsync(Guid ingestJobId, string leaseToken, CancellationToken ct = default)
    {
        var job = await _db.IngestJobs
            .Include(j => j.OriginalArtifact)
            .FirstOrDefaultAsync(j => j.Id == ingestJobId, ct);
        if (job is null) return new IngestResult(false, "job not found");
        if (job.LeaseToken != leaseToken) return new IngestResult(false, "lease mismatch");
        var original = job.OriginalArtifact;

        try
        {
            await IngestArtifactAsync(original, job.Role, ct);
            await _db.IngestJobs
                .Where(j => j.Id == job.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, "completed")
                    .SetProperty(j => j.FinishedAt, DateTime.UtcNow)
                    .SetProperty(j => j.LeaseToken, (string?)null)
                    .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                    .SetProperty(j => j.Error, (string?)null)
                    .SetProperty(j => j.ErrorKind, (string?)null), ct);
            _logger.LogInformation("Ingest job {JobId} completed for artifact {ArtifactId}", job.Id, original.Id);
            return new IngestResult(true, null);
        }
        catch (Exception ex)
        {
            var kind = ex is HttpRequestException ? "dependency" : "processing";
            _logger.LogError(ex, "Ingest job {JobId} failed for artifact {ArtifactId}", job.Id, original.Id);
            await _db.IngestJobs
                .Where(j => j.Id == job.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, "failed")
                    .SetProperty(j => j.FinishedAt, DateTime.UtcNow)
                    .SetProperty(j => j.LeaseToken, (string?)null)
                    .SetProperty(j => j.LeaseUntil, (DateTime?)null)
                    .SetProperty(j => j.ErrorKind, kind)
                    .SetProperty(j => j.Error, ex.Message), ct);
            return new IngestResult(false, ex.Message);
        }
    }

    public async Task<bool> EnsureIngestedAsync(Guid originalArtifactId, string role, CancellationToken ct = default)
    {
        // Fast path: pages already exist for this original.
        var hasPages = await _db.Artifacts
            .AnyAsync(a => a.ParentArtifactId == originalArtifactId && a.Kind == "page", ct);
        if (hasPages) return true;

        var artifact = await _db.Artifacts.FirstOrDefaultAsync(a => a.Id == originalArtifactId, ct);
        if (artifact is null) return false;

        try
        {
            await IngestArtifactAsync(artifact, role, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EnsureIngested failed for artifact {ArtifactId}", originalArtifactId);
            return false;
        }
    }

    /// <summary>The actual pipeline: call Python /ingest, write page files
    /// under content-addressed keys, register page artifacts + text.</summary>
    private async Task IngestArtifactAsync(Artifact original, string role, CancellationToken ct)
    {
        // Text-only documents (DOCX/XLSX/XLS) have no pages — register the
        // extracted text on the original artifact itself.
        var absolutePath = _artifacts.AbsolutePath(original.StorageKey);
        if (!_artifacts.Exists(original.StorageKey))
            throw new FileNotFoundException($"Original artifact file missing: {original.StorageKey}");

        var response = await _gradingClient.IngestAsync(absolutePath, formats: ["png200", "jpeg150"], maxPages: MaxPages, cancellationToken: ct);

        foreach (var page in response.Pages)
        {
            foreach (var format in page.Formats)
            {
                // RegisterPageAsync dedups on rendered content hash — a page
                // already rendered (this document or any other) is a no-op.
                var bytes = Convert.FromBase64String(format.DataB64);
                var artifact = await _artifacts.RegisterPageAsync(
                    bytes, ExtensionForFormat(format.Format, format.MediaType),
                    page.Width, page.Height, original.Id, format.Format, ct);
                if (artifact.ParentArtifactId != original.Id)
                {
                    _logger.LogDebug("Ingest page dedup: {Sha} reused from {Parent}",
                        format.Sha256, artifact.ParentArtifactId);
                }
            }
        }

        if (!string.IsNullOrEmpty(response.Text) && string.IsNullOrEmpty(original.TextContent))
        {
            original.TextContent = response.Text;
            await _db.SaveChangesAsync(ct);
        }

        // Record the source document's true page count (for page-capped docs).
        if (original.PageCount is null && response.TotalPages > 0)
        {
            original.PageCount = response.TotalPages;
            await _db.SaveChangesAsync(ct);
        }

        if (response.Warnings.Count > 0)
        {
            _logger.LogWarning("Ingest warnings for {ArtifactId}: {Warnings}",
                original.Id, string.Join("; ", response.Warnings));
        }
    }

    private static string ExtensionForFormat(string format, string mediaType) => format switch
    {
        "png200" => ".png",
        "jpeg150" => ".jpg",
        _ => mediaType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            _ => ".bin",
        },
    };
}
