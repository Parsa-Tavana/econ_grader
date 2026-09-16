namespace EconGrader.Domain.Entities;

/// <summary>
/// Per-answer, per-page reference to a rendered answer page artifact.
/// An Answer with rows here has been ingested — grading consumes these pages
/// directly (zero conversion at request time). Ordering within the answer is
/// SortOrder, NOT Artifact.PageNumber (pages dedup across documents, so a
/// reused artifact row may carry another document's page number).
/// </summary>
public class AnswerPage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AnswerId { get; set; }
    public Answer Answer { get; set; } = null!;
    public Guid ArtifactId { get; set; }
    public Artifact Artifact { get; set; } = null!;
    /// <summary>"png200" | "jpeg150" — which ingest render this row points at.</summary>
    public string Format { get; set; } = null!;
    /// <summary>0-based position within the answer stack.</summary>
    public int SortOrder { get; set; }
}

/// <summary>
/// Ingest pipeline job: render/normalize one original artifact into page
/// artifacts + extracted text. Lease-based (see GradingJob in M2 for the
/// pattern); idempotent — re-running a completed job is a no-op because page
/// registration dedups by content hash.
/// </summary>
public class IngestJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The original artifact to render.</summary>
    public Guid OriginalArtifactId { get; set; }
    public Artifact OriginalArtifact { get; set; } = null!;

    public string Status { get; set; } = "pending"; // pending|running|completed|failed
    public int Attempts { get; set; }
    public string? ErrorKind { get; set; }
    public string? Error { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary>Lease claim: the token the claiming worker generated and the
    /// time the lease expires. A worker heartbeats LeaseUntil while it runs;
    /// an expired lease is re-claimable by any worker.</summary>
    public string? LeaseToken { get; set; }
    public DateTime? LeaseUntil { get; set; }

    /// <summary>"answer" | "question" | "rubric" | "answerkey" — which upload
    /// role triggered this ingest (informational; pages/text are role-agnostic).</summary>
    public string Role { get; set; } = "answer";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
