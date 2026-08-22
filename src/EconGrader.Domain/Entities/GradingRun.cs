namespace EconGrader.Domain.Entities;

public class GradingRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AnswerId { get; set; }
    public Answer Answer { get; set; } = null!;
    public Guid QuestionId { get; set; }
    public Question Question { get; set; } = null!;
    public Guid StudentId { get; set; }
    public Student Student { get; set; } = null!;
    
    public string Provider { get; set; } = null!; // "Claude", "Gemini", "Qwen"
    public string ModelName { get; set; } = null!; // "claude-3-5-sonnet-20241022"
    public string? ModelVersion { get; set; }
    public decimal Temperature { get; set; }
    public string PromptVersion { get; set; } = null!;
    
    public decimal AiScore { get; set; }
    public decimal? TeacherScoreSnapshot { get; set; } // copied at run time for audit
    public string RawAiResponse { get; set; } = null!; // full JSON, unmodified
    public bool IsValid { get; set; }
    public string? ValidationErrorsJson { get; set; }
    public string? CriteriaScoresJson { get; set; } // per-criterion scores
    public string? Reasoning { get; set; }
    
    public long LatencyMs { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public string? Error { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<TeacherReview> TeacherReviews { get; set; } = new List<TeacherReview>();
}

public class TeacherReview
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GradingRunId { get; set; }
    public GradingRun GradingRun { get; set; } = null!;
    public Guid TeacherUserId { get; set; }
    public User Teacher { get; set; } = null!;
    public decimal OldAiScore { get; set; }
    public decimal NewScore { get; set; } // teacher's override/acceptance
    public string? Note { get; set; }
    public DateTime ReviewedAt { get; set; } = DateTime.UtcNow;
    public ReviewAction Action { get; set; }
}

public enum ReviewAction { Accept = 0, Override = 1 }