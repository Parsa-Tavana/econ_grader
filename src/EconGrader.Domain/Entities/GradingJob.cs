namespace EconGrader.Domain.Entities;

/// <summary>
/// One queued unit of AI grading work (M2). Ensemble runs = N jobs with the
/// same (AnswerId, Temperature, PromptVersion) but distinct EnsembleIndex —
/// the unique index is the idempotency key: a lease-expiry double-claim can
/// never insert a second run for the same logical work.
/// </summary>
public class GradingJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AnswerId { get; set; }
    public decimal Temperature { get; set; }
    public string PromptVersion { get; set; } = "default";
    public int EnsembleIndex { get; set; }

    /// <summary>pending | running | completed | failed | cancelled.</summary>
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public string? ErrorKind { get; set; }
    public string? Error { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? LeaseToken { get; set; }
    public DateTime? LeaseUntil { get; set; }

    /// <summary>The completed run (set on success) — the UI links here.</summary>
    public Guid? GradingRunId { get; set; }
    public GradingRun? GradingRun { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
