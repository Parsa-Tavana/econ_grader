using EconGrader.Application.Data;
using EconGrader.Application.Exceptions;
using EconGrader.Application.Interfaces;
using EconGrader.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EconGrader.Application.Services;

/// <summary>Queued grading (M2): enqueue → lease-claim → execute → complete.
/// The synchronous path (GradingOrchestrationService) stays behind the
/// Grading:Mode flag; this service is the queue path.</summary>
public interface IGradingJobService
{
    /// <summary>Enqueue one job per ensemble slot. Returns existing
    /// pending/running jobs unchanged (idempotent re-enqueue).</summary>
    Task<IReadOnlyList<GradingJob>> EnqueueAsync(
        Guid answerId, decimal temperature, string promptVersion, int runs, Guid userId, CancellationToken ct = default);

    /// <summary>Claim the next pending job — respects the GLOBAL in-flight
    /// cap (running jobs across ALL workers &lt; N), so --scale worker=N never
    /// multiplies gateway load. Returns null when nothing is claimable.</summary>
    Task<GradingJob?> ClaimNextAsync(CancellationToken ct = default);

    /// <summary>Renew a held lease (worker heartbeat, ~60s while executing).</summary>
    Task HeartbeatAsync(Guid jobId, string leaseToken, CancellationToken ct = default);

    /// <summary>Execute a claimed job: grade via the orchestrator, persist the
    /// run, mark completed/failed. LeaseToken mismatch → the job was
    /// re-claimed after expiry; abandon silently (the new owner continues).</summary>
    Task ExecuteJobAsync(GradingJob job, string leaseToken, CancellationToken ct = default);

    /// <summary>Run the worker loop until the host shuts down (AppRole=worker).</summary>
    Task RunWorkerLoopAsync(CancellationToken ct);
}

public sealed class GradingJobOptions
{
    /// <summary>GLOBAL max concurrent grading runs across all workers — the
    /// gateway budget. Scaling workers adds failover, not concurrency.</summary>
    public int MaxInFlight { get; set; } = 4;

    /// <summary>Lease duration (seconds) — must exceed p99 grading latency ×2
    /// so a slow-but-alive call is never double-claimed.</summary>
    public int LeaseSeconds { get; set; } = 600;

    /// <summary>Seconds between worker polls when the queue is empty.</summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>Seconds between lease-renewal heartbeats while executing.</summary>
    public int HeartbeatSeconds { get; set; } = 60;

    /// <summary>Max attempts before a job is marked failed permanently.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>M5 ensemble temperature stagger — per-ensemble-slot step added
    /// to the requested temperature (slot 0 keeps it exactly). Default 0 keeps
    /// every slot identical (plan: "optional … default stays 0.0"). A small
    /// positive value (e.g. 0.1) makes ensemble runs genuinely different
    /// samples instead of N near-copies.</summary>
    public decimal EnsembleStagger { get; set; } = 0m;

    /// <summary>Temperature for ensemble slot <paramref name="index"/> — the
    /// single source for both the queue enqueue and the sync loop, so the two
    /// modes agree. Capped at 2.0 (the UI/API temperature contract).</summary>
    public decimal TemperatureForIndex(decimal baseTemperature, int index) =>
        index == 0 ? baseTemperature : Math.Min(2m, baseTemperature + index * EnsembleStagger);
}

public sealed class GradingJobService : IGradingJobService
{
    // Concrete AppDbContext (not IAppDbContext): the claim needs
    // DbContext.Database for the serializable transaction that keeps the
    // global in-flight cap honest under concurrent workers (same tradeoff
    // as ArtifactStore, which needs .Entry()).
    private readonly AppDbContext _db;
    private readonly IGradingOrchestrationService _orchestrator;
    private readonly IOptions<GradingJobOptions> _options;
    private readonly ILogger<GradingJobService> _logger;

    public GradingJobService(
        AppDbContext db,
        IGradingOrchestrationService orchestrator,
        IOptions<GradingJobOptions> options,
        ILogger<GradingJobService> logger)
    {
        _db = db;
        _orchestrator = orchestrator;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GradingJob>> EnqueueAsync(
        Guid answerId, decimal temperature, string promptVersion, int runs, Guid userId, CancellationToken ct = default)
    {
        var jobs = new List<GradingJob>();
        for (var i = 0; i < runs; i++)
        {
            // M5: optional per-slot temperature stagger (default 0 → identical
            // slots, exactly the old behavior). Slot 0 always keeps the
            // requested temperature verbatim.
            var slotTemperature = _options.Value.TemperatureForIndex(temperature, i);
            // Idempotent: the unique (AnswerId, Temperature, PromptVersion,
            // EnsembleIndex) index backs this up. Pending/running/failed →
            // reuse the row; completed → the work is done, nothing to add.
            var existing = await _db.GradingJobs.FirstOrDefaultAsync(j =>
                j.AnswerId == answerId &&
                j.Temperature == slotTemperature &&
                j.PromptVersion == promptVersion &&
                j.EnsembleIndex == i &&
                j.Status != "completed" &&
                j.Status != "cancelled", ct);
            if (existing is not null)
            {
                // A failed job can be retried by resetting it to pending.
                if (existing.Status == "failed" && existing.Attempts < _options.Value.MaxAttempts)
                {
                    existing.Status = "pending";
                    existing.Error = null;
                    existing.ErrorKind = null;
                    existing.LeaseToken = null;
                    existing.LeaseUntil = null;
                }
                jobs.Add(existing);
                continue;
            }

            var job = new GradingJob
            {
                AnswerId = answerId,
                Temperature = slotTemperature,
                PromptVersion = promptVersion,
                EnsembleIndex = i,
                CreatedByUserId = userId,
            };
            _db.GradingJobs.Add(job);
            jobs.Add(job);
        }
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Enqueued {Count} grading job(s) for answer {AnswerId} (user {UserId})",
            jobs.Count, answerId, userId);
        return jobs;
    }

    public async Task<GradingJob?> ClaimNextAsync(CancellationToken ct = default)
    {
        var opts = _options.Value;

        // Candidates: pending, or running with an EXPIRED lease (dead worker).
        var candidates = await _db.GradingJobs
            .Where(j => j.Status == "pending"
                || (j.Status == "running" && (j.LeaseUntil == null || j.LeaseUntil < DateTime.UtcNow)))
            .OrderBy(j => j.CreatedAt)
            .Take(5)
            .ToListAsync(ct);
        if (candidates.Count == 0) return null;

        foreach (var job in candidates)
        {
            var now = DateTime.UtcNow;
            var token = Guid.NewGuid().ToString("N");
            var leaseUntil = now.AddSeconds(opts.LeaseSeconds);

            // Count-then-claim inside ONE SERIALIZABLE transaction: two
            // workers racing cannot both pass the cap check (SQL Server
            // range-locks the scanned rows; the loser retries). READ
            // COMMITTED would let both see the same pre-claim count and
            // overshoot the budget. Executed via the retrying execution
            // strategy (EnableRetryOnFailure) since Serializable can deadlock.
            var strategy = _db.Database.CreateExecutionStrategy();
            var claimed = await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable, ct);
                try
                {
                    var runningCount = await _db.GradingJobs
                        .Where(j => j.Status == "running" && j.LeaseUntil > now)
                        .CountAsync(ct);
                    if (runningCount >= opts.MaxInFlight)
                    {
                        await tx.CommitAsync(ct);
                        return 0;
                    }

                    var rows = await _db.GradingJobs
                        .Where(j => j.Id == job.Id &&
                            (j.Status == "pending" ||
                             (j.Status == "running" && (j.LeaseUntil == null || j.LeaseUntil < now))))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.Status, "running")
                            .SetProperty(j => j.LeaseToken, token)
                            .SetProperty(j => j.LeaseUntil, leaseUntil)
                            .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                            .SetProperty(j => j.StartedAt, j => j.StartedAt ?? now)
                            .SetProperty(j => j.Error, (string?)null)
                            .SetProperty(j => j.ErrorKind, (string?)null), ct);
                    await tx.CommitAsync(ct);
                    return rows;
                }
                catch
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    throw;
                }
            });

            if (claimed == 1)
            {
                job.Status = "running";
                job.LeaseToken = token;
                job.LeaseUntil = leaseUntil;
                job.Attempts++;
                _logger.LogInformation("Claimed grading job {JobId} (attempt {Attempts}, lease until {LeaseUntil})",
                    job.Id, job.Attempts, leaseUntil);
                return job;
            }
            // claimed == 0: row raced into another worker's claim (or cap
            // filled between candidates read and claim) — try the next one.
        }
        return null;
    }

    public async Task HeartbeatAsync(Guid jobId, string leaseToken, CancellationToken ct = default)
    {
        var leaseSeconds = _options.Value.LeaseSeconds;
        var rows = await _db.GradingJobs
            .Where(j => j.Id == jobId && j.LeaseToken == leaseToken && j.Status == "running")
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.LeaseUntil, DateTime.UtcNow.AddSeconds(leaseSeconds)), ct);
        if (rows == 0)
            _logger.LogWarning("Grading job {JobId} heartbeat rejected (lease lost) — worker should abort", jobId);
    }

    public async Task ExecuteJobAsync(GradingJob job, string leaseToken, CancellationToken ct = default)
    {
        // Idempotency (last line of defense): a lease-expiry re-claim of a job
        // whose run ALREADY persisted must not grade twice — complete it now.
        var fresh = await _db.GradingJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == job.Id, ct);
        if (fresh?.GradingRunId is { } doneRunId)
        {
            await _db.GradingJobs
                .Where(j => j.Id == job.Id && j.LeaseToken == leaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, "completed")
                    .SetProperty(j => j.FinishedAt, DateTime.UtcNow)
                    .SetProperty(j => j.LeaseToken, (string?)null)
                    .SetProperty(j => j.LeaseUntil, (DateTime?)null), ct);
            _logger.LogInformation("Grading job {JobId} already completed as run {RunId} — skipped duplicate execution",
                job.Id, doneRunId);
            return;
        }

        try
        {
            // Grade while heartbeating concurrently: a legitimately slow
            // gateway call (multi-page PDF, gateway retry) must NEVER lose its
            // lease mid-flight — only a genuinely dead worker stops
            // heartbeating, lets the lease expire, and gets re-claimed.
            var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = HeartbeatLoopAsync(job.Id, leaseToken, heartbeatCts.Token);
            try
            {
                var result = await _orchestrator.GradeAnswerAsync(
                    job.AnswerId, job.Temperature, job.PromptVersion, ct);
                var run = result.Runs[0];
                var finished = DateTime.UtcNow;
                var rows = await _db.GradingJobs
                    .Where(j => j.Id == job.Id && j.LeaseToken == leaseToken)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, "completed")
                        .SetProperty(j => j.GradingRunId, run.Id)
                        .SetProperty(j => j.FinishedAt, finished)
                        .SetProperty(j => j.LeaseToken, (string?)null)
                        .SetProperty(j => j.LeaseUntil, (DateTime?)null), ct);
                if (rows == 0)
                    _logger.LogWarning("Grading job {JobId} completed but lease was lost — run {RunId} still persisted (idempotency guard: re-claim detects the run and skips re-grading)", job.Id, run.Id);
                else
                    _logger.LogInformation("Grading job {JobId} completed → run {RunId} (valid={IsValid})",
                        job.Id, run.Id, run.IsValid);
            }
            finally
            {
                heartbeatCts.Cancel();
                try { await heartbeat; } catch (OperationCanceledException) { /* expected */ }
                heartbeatCts.Dispose();
            }
            return;
        }
        catch (OperationCanceledException)
        {
            // Host shutdown: release the lease so another worker picks it up.
            await _db.GradingJobs
                .Where(j => j.Id == job.Id && j.LeaseToken == leaseToken && j.Status == "running")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, "pending")
                    .SetProperty(j => j.LeaseToken, (string?)null)
                    .SetProperty(j => j.LeaseUntil, (DateTime?)null), ct);
            throw;
        }
        catch (Exception ex)
        {
            var kind = ex is DependencyException ? "dependency" :
                       ex is NotFoundException ? "not_found" :
                       ex is BusinessRuleException ? "business_rule" : "internal";
            var failed = job.Attempts >= _options.Value.MaxAttempts;
            var finished = DateTime.UtcNow;
            var rows = await _db.GradingJobs
                .Where(j => j.Id == job.Id && j.LeaseToken == leaseToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, failed ? "failed" : "pending")
                    .SetProperty(j => j.ErrorKind, kind)
                    .SetProperty(j => j.Error, Truncate(ex.Message))
                    .SetProperty(j => j.FinishedAt, failed ? finished : (DateTime?)null)
                    .SetProperty(j => j.LeaseToken, (string?)null)
                    .SetProperty(j => j.LeaseUntil, (DateTime?)null), ct);
            _logger.LogError(ex, "Grading job {JobId} failed (attempt {Attempts}, {Outcome})",
                job.Id, job.Attempts, failed ? "permanent" : "requeued");
        }
    }

    public async Task RunWorkerLoopAsync(CancellationToken ct)
    {
        var opts = _options.Value;
        _logger.LogInformation("Grading worker loop started (maxInFlight={Max}, lease={Lease}s, poll={Poll}s)",
            opts.MaxInFlight, opts.LeaseSeconds, opts.PollIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var job = await ClaimNextAsync(ct);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(opts.PollIntervalSeconds), ct);
                    continue;
                }
                // Sequential per worker; GLOBAL concurrency across workers is
                // enforced inside ClaimNextAsync (the actual budget).
                await ExecuteJobAsync(job, job.LeaseToken!, ct);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker loop iteration failed — retrying in {Seconds}s", opts.PollIntervalSeconds);
                try { await Task.Delay(TimeSpan.FromSeconds(opts.PollIntervalSeconds), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        _logger.LogInformation("Grading worker loop stopped");
    }

    /// <summary>Renew the lease every HeartbeatSeconds while the job runs —
    /// a slow-but-alive call keeps its lease; only a dead worker (no
    /// heartbeats) lets it expire and gets re-claimed.</summary>
    private async Task HeartbeatLoopAsync(Guid jobId, string leaseToken, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(10, _options.Value.HeartbeatSeconds));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);
                await HeartbeatAsync(jobId, leaseToken, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { /* normal stop after grading ends */ }
    }

    private static string? Truncate(string? s) =>
        string.IsNullOrEmpty(s) ? s : s.Length <= 2000 ? s : s[..2000];
}
