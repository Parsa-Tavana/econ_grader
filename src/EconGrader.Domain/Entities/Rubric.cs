namespace EconGrader.Domain.Entities;

public class Rubric
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid QuestionId { get; set; }
    public Question Question { get; set; } = null!;
    public int Version { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedByUserId { get; set; }
    /// <summary>Optional stored rubric document (PDF/PNG/JPG/DOCX).</summary>
    public string? FileStorageKey { get; set; }
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public ICollection<RubricCriterion> Criteria { get; set; } = new List<RubricCriterion>();
    public decimal TotalMaxScore => Criteria.Sum(c => c.MaxScore);
}

public class RubricCriterion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RubricId { get; set; }
    public Rubric Rubric { get; set; } = null!;
    public string CriterionId { get; set; } = null!; // e.g. "1a", "2b"
    public string Description { get; set; } = null!;
    public decimal MaxScore { get; set; }
    public int Order { get; set; }
}
