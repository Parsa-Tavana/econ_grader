namespace EconGrader.Domain.Entities;

/// <summary>
/// M6 bulk answer-sheet split: ONE merged multi-student PDF (or image) is
/// split into per-student answers. The batch row doubles as its own queue
/// job — Status="splitting" with a lease (same pattern as IngestJob) is the
/// claimable state; the worker that wins the lease OCRs the header band of
/// every page and writes BulkPageMapping rows.
///
/// Idempotent by construction: the (QuestionId, SourceArtifactId) identity
/// means re-uploading the same merged file returns the EXISTING batch (no
/// re-OCR), and Confirm→apply is guarded by Status.
/// </summary>
public class BulkAnswerBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Which question these answers belong to (one batch = one
    /// question's answer stack).</summary>
    public Guid QuestionId { get; set; }
    public Question Question { get; set; } = null!;

    /// <summary>The merged upload, content-addressed. Same hash re-uploaded →
    /// the same batch (the idempotency anchor).</summary>
    public Guid SourceArtifactId { get; set; }
    public Artifact SourceArtifact { get; set; } = null!;

    /// <summary>Original merged-file name/type — copied onto every Answer this
    /// batch applies (mirrors the single-upload fields).</summary>
    public string? SourceFileName { get; set; }
    public string? SourceContentType { get; set; }

    /// <summary>splitting | ready_for_review | applied | failed.</summary>
    public string Status { get; set; } = "splitting";
    public int TotalPages { get; set; }

    public Guid CreatedByUserId { get; set; }

    // ── Lease (the batch is its own queue job) ──────────────────────────────
    public int Attempts { get; set; }
    public string? ErrorKind { get; set; }
    public string? Error { get; set; }
    public string? LeaseToken { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One page of a bulk batch: which rendered page artifact it is, what the
/// header-band OCR read as the student identifier, and how confident the
/// read was. Grouping into per-student stacks is expressed by
/// MatchedStudentId + SortInStack; rows the teacher has not confirmed keep
/// their ReviewStatus so the review UI can show exactly what was automatic.
///
/// SAFETY RULES (locked): an OCR id below the confidence threshold is NEVER
/// auto-assigned; an id matching no Student row is NEVER auto-turned into a
/// Student — the teacher maps or creates students in the review UI.
/// </summary>
public class BulkPageMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BatchId { get; set; }
    public BulkAnswerBatch Batch { get; set; } = null!;

    /// <summary>Rendered page artifact (preferred grading format). Unique
    /// (BatchId, PageNumber) fixes document order even when page dedup reused
    /// an artifact row first registered by another document.</summary>
    public Guid PageArtifactId { get; set; }
    public Artifact PageArtifact { get; set; } = null!;

    /// <summary>1-based position within the merged source document.</summary>
    public int PageNumber { get; set; }

    /// <summary>Normalized OCR read (Persian digits → ASCII); null = nothing
    /// readable. Kept verbatim so the review UI shows what was seen.</summary>
    public string? RawOcrId { get; set; }

    /// <summary>0..1 tesseract word-confidence for the header band.</summary>
    public decimal OcrConfidence { get; set; }

    /// <summary>Student this page is assigned to (null until matched by OCR
    /// to an existing student or assigned by the teacher).</summary>
    public Guid? MatchedStudentId { get; set; }
    public Student? MatchedStudent { get; set; }

    /// <summary>auto | needs_review | unmatched | confirmed | skipped.
    /// auto = OCR matched an existing student at/above threshold; unmatched =
    /// nothing readable; needs_review = flagged (low confidence, unknown id,
    /// or non-consecutive re-appearance); confirmed/skipped = teacher action.</summary>
    public string ReviewStatus { get; set; } = "unmatched";

    /// <summary>0-based position within the student's page stack.</summary>
    public int SortInStack { get; set; }
}
