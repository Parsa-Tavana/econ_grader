namespace EconGrader.Domain.Entities;

public class Exam
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
    /// <summary>The exam's date (day precision) — shown on cards and used for sorting.</summary>
    public DateOnly ExamDate { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public User CreatedBy { get; set; } = null!;
    /// <summary>Optional exam-wide rubric document (the grading key) — the
    /// source the AI extracts all questions + rubric criteria from.</summary>
    public string? RubricFileStorageKey { get; set; }
    public string? RubricFileName { get; set; }
    public string? RubricFileContentType { get; set; }
    /// <summary>Original artifact registered for the rubric document upload
    /// (content-hash addressable; null for legacy uploads before M1 and until
    /// backfill runs).</summary>
    public Guid? RubricFileArtifactId { get; set; }
    public Artifact? RubricFileArtifact { get; set; }
    /// <summary>پاسخنامه gate — when ON, the grader receives each question's
    /// QuestionAnswerKey pages (the examiner's model answer) as the grading
    /// reference. Default OFF; ships inert in M1, flipped per-exam only after
    /// the M4 golden-set gate passes. Uploading a پاسخنامه alone never
    /// changes grading behavior.</summary>
    public bool GroundTruthGradingEnabled { get; set; } = false;
    public ICollection<Question> Questions { get; set; } = new List<Question>();
}
