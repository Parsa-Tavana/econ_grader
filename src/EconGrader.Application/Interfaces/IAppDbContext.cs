using EconGrader.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace EconGrader.Application.Interfaces;

public interface IAppDbContext
{
    DbSet<User> Users { get; }
    DbSet<Exam> Exams { get; }
    DbSet<ExamCorrector> ExamCorrectors { get; }
    DbSet<Question> Questions { get; }
    DbSet<Rubric> Rubrics { get; }
    DbSet<RubricCriterion> RubricCriteria { get; }
    DbSet<Student> Students { get; }
    DbSet<Answer> Answers { get; }
    DbSet<GradingRun> GradingRuns { get; }
    DbSet<TeacherReview> TeacherReviews { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<ModelConfig> ModelConfigs { get; }
    DbSet<Artifact> Artifacts { get; }
    DbSet<QuestionAsset> QuestionAssets { get; }
    DbSet<QuestionAnswerKey> QuestionAnswerKeys { get; }
    DbSet<AnswerPage> AnswerPages { get; }
    DbSet<IngestJob> IngestJobs { get; }
    DbSet<GradingJob> GradingJobs { get; }
    DbSet<EvalRun> EvalRuns { get; }
    DbSet<BulkAnswerBatch> BulkAnswerBatches { get; }
    DbSet<BulkPageMapping> BulkPageMappings { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}