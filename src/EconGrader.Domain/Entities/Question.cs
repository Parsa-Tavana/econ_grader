namespace EconGrader.Domain.Entities;

public class Question
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExamId { get; set; }
    public Exam Exam { get; set; } = null!;
    public int Number { get; set; }
    public string Text { get; set; } = null!;
    public decimal MaxScore { get; set; }
    public int DisplayOrder { get; set; }
    /// <summary>Optional stored file (PDF/PNG/JPG/DOCX) of the question paper.</summary>
    public string? FileStorageKey { get; set; }
    /// <summary>Original artifact registered for the question file (content-hash
    /// addressable; null for legacy uploads before M1 and until backfill runs).
    /// Coexists with FileStorageKey — the legacy key keeps working.</summary>
    public Guid? FileArtifactId { get; set; }
    public Artifact? FileArtifact { get; set; }
    /// <summary>Original artifact registered for the پاسخنامه (model answer)
    /// file, when the examiner's key arrives as a per-question file. Coexists
    /// with the legacy storage key below.</summary>
    public Guid? AnswerKeyArtifactId { get; set; }
    public Artifact? AnswerKeyArtifact { get; set; }
    public string? AnswerKeyStorageKey { get; set; }
    public string? AnswerKeyFileName { get; set; }
    public string? AnswerKeyContentType { get; set; }
    public ICollection<QuestionAsset> Assets { get; set; } = new List<QuestionAsset>();
    public ICollection<QuestionAnswerKey> AnswerKeys { get; set; } = new List<QuestionAnswerKey>();
    /// <summary>Original file name shown to the user (safe to display).</summary>
    public string? FileName { get; set; }
    /// <summary>MIME type, e.g. application/pdf — used for download headers and AI input routing.</summary>
    public string? ContentType { get; set; }
    public ICollection<Rubric> Rubrics { get; set; } = new List<Rubric>();
    public ICollection<Answer> Answers { get; set; } = new List<Answer>();
    public ICollection<GradingRun> GradingRuns { get; set; } = new List<GradingRun>();
}