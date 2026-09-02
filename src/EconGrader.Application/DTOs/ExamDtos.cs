namespace EconGrader.Application.DTOs;

public record ExamDto(
    Guid Id,
    string Name,
    int Year,
    string? Description,
    DateTime CreatedAt,
    string CreatedByName,
    string? RubricFileName = null,
    string? RubricFileContentType = null
);

public record CreateExamRequest(
    string Name,
    int Year,
    string? Description
);

public record UpdateExamRequest(
    string Name,
    int Year,
    string? Description
);