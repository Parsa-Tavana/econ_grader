namespace EconGrader.Domain.Entities;

/// <summary>
/// Numeric values are part of the persisted contract (Role is stored as int):
/// Teacher=0 and Admin=1 predate Corrector/Student, so appending new roles —
/// never renumbering — keeps existing rows meaningful across the migration.
/// </summary>
public enum UserRole { Teacher = 0, Admin = 1, Corrector = 2, Student = 3 }

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = null!;
    /// <summary>Versioned PBKDF2 blob: "pbkdf2.{iterations}.{saltB64}.{hashB64}".
    /// Salt travels inside the blob — one column, atomic with the hash.
    /// Legacy sentinel "external-identity" never matches a login attempt.</summary>
    public string PasswordHash { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public UserRole Role { get; set; }
    /// <summary>Inverse of Student.UserId — populated only for Role=Student logins.</summary>
    public Student? Student { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}
