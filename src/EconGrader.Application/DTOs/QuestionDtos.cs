namespace EconGrader.Application.DTOs;

public record QuestionDto(
    Guid Id,
    Guid ExamId,
    int Number,
    string Text,
    decimal MaxScore,
    string? RubricText
);

public record CreateQuestionRequest(
    Guid ExamId,
    int Number,
    string Text,
    decimal MaxScore,
    string? RubricText
);

public record RubricCriterionDto(
    string CriterionId,
    string Description,
    decimal MaxScore,
    int Order
);

public record RubricDto(
    Guid Id,
    Guid QuestionId,
    int Version,
    bool IsActive,
    DateTime CreatedAt,
    decimal TotalMaxScore,
    IReadOnlyList<RubricCriterionDto> Criteria
);

public record CreateRubricRequest(
    Guid QuestionId,
    IReadOnlyList<CreateRubricCriterionRequest> Criteria
);

public record CreateRubricCriterionRequest(
    string CriterionId,
    string Description,
    decimal MaxScore
);