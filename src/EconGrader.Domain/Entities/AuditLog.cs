namespace EconGrader.Domain.Entities;

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = null!; // GradingRequest, GradingResponse, TeacherReview, AdminEdit, AuthLogin, etc.
    public string EntityType { get; set; } = null!;
    public string? EntityId { get; set; }
    public Guid? UserId { get; set; }
    public User? User { get; set; }
    public string? Details { get; set; } // JSON payload (keys sanitized)
    public string? IpAddress { get; set; }
}