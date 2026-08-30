namespace EconGrader.Domain.Entities;

/// <summary>
/// Assignment of a Corrector (User with Role=Corrector) to one Exam.
/// Correctors' read/review scope is exactly the set of exams they're
/// assigned to via this table; it deliberately carries no authoring rights.
/// </summary>
public class ExamCorrector
{
    public Guid ExamId { get; set; }
    public Exam Exam { get; set; } = null!;
    public Guid CorrectorUserId { get; set; }
    public User Corrector { get; set; } = null!;
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}
