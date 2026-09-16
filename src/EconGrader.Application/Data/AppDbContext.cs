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
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<QuestionAsset> QuestionAssets => Set<QuestionAsset>();
    public DbSet<QuestionAnswerKey> QuestionAnswerKeys => Set<QuestionAnswerKey>();
    public DbSet<AnswerPage> AnswerPages => Set<AnswerPage>();
    public DbSet<IngestJob> IngestJobs => Set<IngestJob>();
    public DbSet<GradingJob> GradingJobs => Set<GradingJob>();
    public DbSet<EvalRun> EvalRuns => Set<EvalRun>();
    public DbSet<BulkAnswerBatch> BulkAnswerBatches => Set<BulkAnswerBatch>();
    public DbSet<BulkPageMapping> BulkPageMappings => Set<BulkPageMapping>();

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
            // Rubric-file artifact FK: Restrict (SQL Server rejects SetNull
            // here with error 1785 — a cascade path through Artifacts' other
            // FKs). Artifacts are content-addressed and never deleted via
            // FK paths, so NO ACTION behaves identically in practice.
            e.HasOne(x => x.RubricFileArtifact).WithMany()
                .HasForeignKey(x => x.RubricFileArtifactId).OnDelete(DeleteBehavior.Restrict);
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
            e.Property(x => x.AnswerKeyFileName).HasMaxLength(260);
            e.Property(x => x.AnswerKeyContentType).HasMaxLength(128);
            // Question-file + answer-key artifact FKs: Restrict (same
            // SQL Server cascade-path rule as Exam.RubricFileArtifact).
            e.HasOne(x => x.FileArtifact).WithMany()
                .HasForeignKey(x => x.FileArtifactId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AnswerKeyArtifact).WithMany()
                .HasForeignKey(x => x.AnswerKeyArtifactId).OnDelete(DeleteBehavior.Restrict);
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
            e.HasOne(x => x.OriginalArtifact).WithMany()
                .HasForeignKey(x => x.OriginalArtifactId).OnDelete(DeleteBehavior.Restrict);
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

        // Artifact — one row per stored blob/page. The (Sha256, Kind,
        // PageNumber) unique index is the dedup guarantee: an ingest that finds
        // the row already there can reuse it instead of re-rendering.
        // Kind is a short string ("original" | "page"); StorageKey/Sha256 are
        // fixed-length hex/paths (64-char hashes live inside them).
        b.Entity<Artifact>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.StorageKey).HasMaxLength(256);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.Format).HasMaxLength(16);
            // Extracted document text can be large (extraction caps at 150k
            // chars) — NVARCHAR(MAX) is EF's default for string without
            // MaxLength; kept unlimited deliberately.
            // (Sha256, Kind) unique — NOT including PageNumber: pages dedup
            // purely on rendered bytes (PageNumber is informational and may
            // repeat across documents), and a nullable column in a unique
            // index would exclude originals (PageNumber NULL) entirely.
            e.HasIndex(x => new { x.Sha256, x.Kind }).IsUnique();
            e.HasIndex(x => x.StorageKey);
            // Parent lineage: pages point at the original they were rendered
            // from. Restrict, NOT Cascade — SQL Server rejects a self-
            // referencing cascade FK on the same table with error 1785
            // ("cycles or multiple cascade paths"), which made the M1
            // migration unappliable. Artifacts are content-addressed and
            // never deleted via FK paths anyway (upload replacement deletes
            // by storage key); nothing in the code deletes an original row.
            e.HasOne(x => x.Parent).WithMany()
                .HasForeignKey(x => x.ParentArtifactId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // QuestionAsset — rendered question-paper pages linked to the item.
        b.Entity<QuestionAsset>(e =>
        {
            e.HasOne(x => x.Question).WithMany(x => x.Assets).HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Artifact).WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Role).HasMaxLength(32);
            e.HasIndex(x => new { x.QuestionId, x.SortOrder });
        });

        // QuestionAnswerKey — rendered پاسخنامه pages linked to the item.
        b.Entity<QuestionAnswerKey>(e =>
        {
            e.HasOne(x => x.Question).WithMany(x => x.AnswerKeys).HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Artifact).WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Role).HasMaxLength(32);
            e.HasIndex(x => new { x.QuestionId, x.SortOrder });
        });

        // AnswerPage — per-answer rendered page reference.
        b.Entity<AnswerPage>(e =>
        {
            e.HasOne(x => x.Answer).WithMany().HasForeignKey(x => x.AnswerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Artifact).WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Format).HasMaxLength(16);
            e.HasIndex(x => new { x.AnswerId, x.Format, x.SortOrder });
        });

        // GradingJob — queued AI grading work (M2). The unique (AnswerId,
        // Temperature, PromptVersion, EnsembleIndex) index is the idempotency
        // key: a lease-expiry double-claim can never insert a second run for
        // the same logical work. CreatedByUserId Restrict — user deletion
        // must not cascade into job history.
        b.Entity<GradingJob>(e =>
        {
            e.HasIndex(x => new { x.AnswerId, x.Temperature, x.PromptVersion, x.EnsembleIndex }).IsUnique();
            e.HasIndex(x => new { x.Status, x.LeaseUntil });
            e.HasIndex(x => x.CreatedAt);
            e.HasOne(x => x.GradingRun).WithMany()
                .HasForeignKey(x => x.GradingRunId).OnDelete(DeleteBehavior.SetNull);
        });

        // EvalRun — golden-set harness history (M4). Append-only evidence:
        // deleting a question must not delete the harness evidence of a
        // prompt/model change that was gated on it.
        b.Entity<EvalRun>(e =>
        {
            e.Property(x => x.ConfigurationJson).HasMaxLength(2000);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.QuestionId);
            // EvalRun question FK: SetNull is fine here (no artifact hop in
            // the path). Kept as designed.
            e.HasOne(x => x.Question).WithMany()
                .HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<IngestJob>(e =>
        {
            e.HasOne(x => x.OriginalArtifact).WithMany().HasForeignKey(x => x.OriginalArtifactId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.ErrorKind).HasMaxLength(32);
            e.Property(x => x.LeaseToken).HasMaxLength(64);
            e.Property(x => x.Role).HasMaxLength(16);
            e.HasIndex(x => new { x.Status, x.LeaseUntil });
            // One pending/running job per artifact — completed jobs are kept
            // as history; a re-ingest of a completed artifact is a no-op.
            e.HasIndex(x => new { x.OriginalArtifactId, x.Status });
        });

        // BulkAnswerBatch — M6 bulk split. The batch IS its own queue job
        // (Status=splitting + lease = claimable). SourceArtifactId Cascade:
        // deleting the merged upload blob row takes its batch history with
        // it. CreatedByUserId Restrict — user deletion never deletes batches.
        b.Entity<BulkAnswerBatch>(e =>
        {
            e.HasOne(x => x.Question).WithMany()
                .HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.SourceArtifact).WithMany()
                .HasForeignKey(x => x.SourceArtifactId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Status).HasMaxLength(24);
            e.Property(x => x.ErrorKind).HasMaxLength(32);
            e.Property(x => x.LeaseToken).HasMaxLength(64);
            e.HasIndex(x => new { x.QuestionId, x.SourceArtifactId }).IsUnique();
            e.HasIndex(x => new { x.Status, x.LeaseUntil });
            e.HasIndex(x => x.CreatedAt);
        });

        // BulkPageMapping — per-page OCR result + assignment. The unique
        // (BatchId, PageNumber) index is the split-run idempotency anchor:
        // a re-run after lease expiry sees the rows already there.
        b.Entity<BulkPageMapping>(e =>
        {
            e.HasOne(x => x.Batch).WithMany()
                .HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.PageArtifact).WithMany()
                .HasForeignKey(x => x.PageArtifactId).OnDelete(DeleteBehavior.Restrict);
            // MatchedStudent: SetNull is safe here — the path Student←… never
            // reaches Artifacts through this table (BulkPageMapping has no
            // artifact cascade inbound), and clearing the assignment when a
            // student is deleted is exactly the wanted behavior.
            e.HasOne(x => x.MatchedStudent).WithMany()
                .HasForeignKey(x => x.MatchedStudentId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.RawOcrId).HasMaxLength(32);
            e.Property(x => x.ReviewStatus).HasMaxLength(16);
            e.HasIndex(x => new { x.BatchId, x.PageNumber }).IsUnique();
            e.HasIndex(x => new { x.BatchId, x.MatchedStudentId });
        });
    }
}