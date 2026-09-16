using EconGrader.Domain.Entities;

namespace EconGrader.Domain.Entities;

/// <summary>
/// Item-banking link: a rendered page of the question's paper belongs to this
/// question's statement. Created at extraction-apply time (page numbers from
/// extraction output / teacher's pick); grading consumes these instead of
/// re-rendering the question PDF per run.
/// </summary>
public class QuestionAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid QuestionId { get; set; }
    public Question Question { get; set; } = null!;
    public Guid ArtifactId { get; set; }
    public Artifact Artifact { get; set; } = null!;
    public int SortOrder { get; set; }
    /// <summary>"question_paper" | "figure".</summary>
    public string Role { get; set; } = "question_paper";
}

/// <summary>
/// Item-banking link: a rendered page of the پاسخنامه (model answer /
/// examiner's key) for this question. Consumed by the grader ONLY when
/// Exam.GroundTruthGradingEnabled is on (the flag is inert until M4 evidence).
/// </summary>
public class QuestionAnswerKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid QuestionId { get; set; }
    public Question Question { get; set; } = null!;
    public Guid ArtifactId { get; set; }
    public Artifact Artifact { get; set; } = null!;
    public int SortOrder { get; set; }
    /// <summary>"model_answer" | "model_figure".</summary>
    public string Role { get; set; } = "model_answer";
}
