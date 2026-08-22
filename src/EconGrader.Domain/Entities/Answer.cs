namespace EconGrader.Domain.Entities;

public class Student
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ExternalId { get; set; } = null!; // e.g. "S001", exam roll number
    public string? DisplayName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<Answer> Answers { get; set; } = new List<Answer>();
}

public class Answer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public Student Student { get; set; } = null!;
    public Guid QuestionId { get; set; }
    public Question Question { get; set; } = null!;
    /// <summary>Relative storage key (e.g. answers/2026/…/page1.png).</summary>
    public string ImageStorageKey { get; set; } = null!;
    /// <summary>Teacher's ground-truth score — NEVER sent to the AI.</summary>
    public decimal? TeacherScore { get; set; }
    /// <summary>Second independent teacher score, for human-human ceiling metrics.</summary>
    public decimal? Teacher2Score { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public ICollection<GradingRun> GradingRuns { get; set; } = new List<GradingRun>();
}
