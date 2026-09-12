using Microsoft.EntityFrameworkCore;
using EconGrader.Domain.Entities;
using EconGrader.Application.Interfaces;

namespace EconGrader.Application.Data;

public sealed class AppDbContext : DbContext, IAppDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Exam> Exams => Set<Exam>();
    public DbSet<ExamCorrector> ExamCorrectors => Set<ExamCorrector>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Rubric> Rubrics => Set<Rubric>();
    public DbSet<RubricCriterion> RubricCriteria => Set<RubricCriterion>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<Answer> Answers => Set<Answer>();
    public DbSet<GradingRun> GradingRuns => Set<GradingRun>();
    public DbSet<TeacherReview> TeacherReviews => Set<TeacherReview>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ModelConfig> ModelConfigs => Set<ModelConfig>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // User
        b.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Role).HasConversion<int>();
        });

        // Exam
        b.Entity<Exam>(e =>
        {
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RubricFileName).HasMaxLength(260);
            e.Property(x => x.RubricFileContentType).HasMaxLength(128);
        });

        // ExamCorrector — Corrector↔Exam assignment (RBAC scope table)
        b.Entity<ExamCorrector>(e =>
        {
            e.HasKey(x => new { x.ExamId, x.CorrectorUserId });
            e.HasOne(x => x.Exam).WithMany().HasForeignKey(x => x.ExamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Corrector).WithMany().HasForeignKey(x => x.CorrectorUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.CorrectorUserId);
        });

        // Question
        b.Entity<Question>(e =>
        {
            e.HasOne(x => x.Exam).WithMany(x => x.Questions).HasForeignKey(x => x.ExamId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ExamId, x.Number }).IsUnique();
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(128);
        });

        // Rubric
        b.Entity<Rubric>(e =>
        {
            e.HasOne(x => x.Question).WithMany(x => x.Rubrics).HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.QuestionId, x.Version }).IsUnique();
        });

        // RubricCriterion
        b.Entity<RubricCriterion>(e =>
        {
            e.HasOne(x => x.Rubric).WithMany(x => x.Criteria).HasForeignKey(x => x.RubricId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RubricId, x.CriterionId }).IsUnique();
        });

        // Student
        b.Entity<Student>(e =>
        {
            e.HasIndex(s => s.ExternalId).IsUnique();
            // Optional login identity: one User (Role=Student) ↔ at most one Student.
            e.HasOne(s => s.User).WithOne(u => u.Student).HasForeignKey<Student>(s => s.UserId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(s => s.UserId).IsUnique().HasFilter("[UserId] IS NOT NULL");
        });

        // Answer
        b.Entity<Answer>(e =>
        {
            e.HasOne(x => x.Student).WithMany(x => x.Answers).HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Question).WithMany(x => x.Answers).HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.StudentId, x.QuestionId }).IsUnique();
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(128);
        });

        // GradingRun - THE core entity
        // NOTE: only the Answer FK may cascade. SQL Server forbids multiple
        // cascade paths (Answer→Question and Answer→Student already cascade),
        // so Question/Student use Restrict — deleting a question or student is
        // still blocked by the Answer-level cascades until its answers are gone.
        b.Entity<GradingRun>(e =>
        {
            e.HasOne(x => x.Answer).WithMany(x => x.GradingRuns).HasForeignKey(x => x.AnswerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Question).WithMany(x => x.GradingRuns).HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.AnswerId, x.CreatedAt });
            e.HasIndex(x => new { x.QuestionId, x.Provider, x.ModelName, x.PromptVersion });
        });

        // TeacherReview - append-only audit trail
        b.Entity<TeacherReview>(e =>
        {
            e.HasOne(x => x.GradingRun).WithMany(x => x.TeacherReviews).HasForeignKey(x => x.GradingRunId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherUserId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Action).HasConversion<int>();
        });

        // AuditLog - append-only, never edited
        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.UserId);
        });

        // ModelConfig
        b.Entity<ModelConfig>(e =>
        {
            e.HasIndex(x => new { x.Provider, x.ModelName }).IsUnique();
        });
    }
}