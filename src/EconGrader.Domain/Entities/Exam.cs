namespace EconGrader.Domain.Entities;

public class Exam
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
    public int Year { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
    public User CreatedBy { get; set; } = null!;
    /// <summary>Optional exam-wide rubric document (the grading key) — the
    /// source the AI extracts all questions + rubric criteria from.</summary>
    public string? RubricFileStorageKey { get; set; }
    public string? RubricFileName { get; set; }
    public string? RubricFileContentType { get; set; }
    public ICollection<Question> Questions { get; set; } = new List<Question>();
}
