namespace EconGrader.Application.DTOs;

public record ExamDto(
    Guid Id,
    string Name,
    DateOnly ExamDate,
    string? Description,
    DateTime CreatedAt,
    string CreatedByName,
    string? RubricFileName = null,
    string? RubricFileContentType = null
);

public record CreateExamRequest(
    string Name,
    DateOnly ExamDate,
    string? Description
);

public record UpdateExamRequest(
    string Name,
    DateOnly ExamDate,
    string? Description
);