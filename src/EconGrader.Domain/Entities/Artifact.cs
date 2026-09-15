namespace EconGrader.Domain.Entities;

/// <summary>
/// One immutable stored file in the content-addressed artifact store.
/// Everything the pipeline consumes (uploads and rendered pages) is registered
/// here so records reference artifacts, never raw paths.
///
/// kind="original" — an uploaded source document; Sha256 is the hash of the
/// uploaded bytes (identical re-uploads dedup to ONE row + ONE file).
/// kind="page" — a page rendered/derived from a parent original at ingest
/// time. Crucially, Sha256 here is the hash of the RENDERED page's own bytes,
/// NOT the parent document's — that is what makes dedup real: the same figure
/// inside two different PDFs renders to identical page bytes → one stored
/// file. The parent's identity lives in ParentArtifactId lineage only.
/// </summary>
public class Artifact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>"original" | "page".</summary>
    public string Kind { get; set; } = null!;
    /// <summary>Content-addressed key under the storage root
    /// (blobs/{h2}/{hash}{ext} for originals, pages/{h2}/{hash}{ext} for
    /// pages — the filename is the content hash, so the same rendered page
    /// always maps to the same file regardless of which document produced it).
    /// Backfilled legacy rows may instead point at their pre-existing key.</summary>
    public string StorageKey { get; set; } = null!;
    public string Sha256 { get; set; } = null!;
    public long Bytes { get; set; }
    /// <summary>Rendered image dimensions (page artifacts only).</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }
    /// <summary>1-based position within the parent document (page artifacts only).
    /// INFORMATIONAL ONLY: dedup may reuse a row first registered for another
    /// document, so ordering within a specific document always comes from the
    /// linking rows (QuestionAsset.SortOrder / AnswerPage.SortOrder), never
    /// from this column.</summary>
    public int? PageNumber { get; set; }
    /// <summary>Which render produced this page: "png200" (legacy-compatible)
    /// or "jpeg150" (the new compact format). Ingest renders BOTH so the
    /// golden-set harness can A/B them; null for original artifacts.</summary>
    public string? Format { get; set; }
    /// <summary>Extracted document text (DOCX/XLSX/XLS originals only) —
    /// captured at ingest so the grader never re-extracts at request time.</summary>
    public string? TextContent { get; set; }
    /// <summary>For pages: the original artifact this page was rendered from.
    /// NOTE: when an identical rendered page already existed (dedup hit), the
    /// row is REUSED with its first parent — reads must not assume every page
    /// of a document is a direct child of that document's original row.</summary>
    public Guid? ParentArtifactId { get; set; }
    public Artifact? Parent { get; set; }
    /// <summary>Total pages in the source document (original artifacts of
    /// paged documents only; null until ingest runs or the source has no
    /// pages). Used to detect a page-capped ingest so grading can fall back
    /// to the legacy whole-file path.</summary>
    public int? PageCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
