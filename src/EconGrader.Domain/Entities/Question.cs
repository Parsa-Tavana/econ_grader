namespace EconGrader.Domain.Entities;

public class Question
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExamId { get; set; }
    public Exam Exam { get; set; } = null!;
    public int Number { get; set; }
    public string Text { get; set; } = null!;
    public decimal MaxScore { get; set; }
    public string? RubricText { get; set; }
    public int DisplayOrder { get; set; }
    /// <summary>Optional stored file (PDF/PNG/JPG/DOCX) of the question paper.</summary>
    public string? FileStorageKey { get; set; }
    /// <summary>Original file name shown to the user (safe to display).</summary>
    public string? FileName { get; set; }
    /// <summary>MIME type, e.g. application/pdf — used for download headers and AI input routing.</summary>
    public string? ContentType { get; set; }
    public ICollection<Rubric> Rubrics { get; set; } = new List<Rubric>();
    public ICollection<Answer> Answers { get; set; } = new List<Answer>();
    public ICollection<GradingRun> GradingRuns { get; set; } = new List<GradingRun>();
}