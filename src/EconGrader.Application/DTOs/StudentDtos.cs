namespace EconGrader.Application.DTOs;

public record StudentDto(
    Guid Id,
    string ExternalId,
    string? DisplayName,
    DateTime CreatedAt
);

public record CreateStudentRequest(
    string ExternalId,
    string? DisplayName
);

public record AnswerDto(
    Guid Id,
    Guid StudentId,
    string StudentExternalId,
    Guid QuestionId,
    string ImageStorageKey,
    decimal? TeacherScore,
    decimal? Teacher2Score,
    DateTime UploadedAt,
    IReadOnlyList<GradingRunSummaryDto> GradingRuns
);

public record GradingRunSummaryDto(
    Guid Id,
    string Provider,
    string ModelName,
    string PromptVersion,
    decimal Temperature,
    decimal AiScore,
    decimal? TeacherScoreSnapshot,
    bool IsValid,
    string? Error,
    DateTime CreatedAt
);