namespace EconGrader.Application.DTOs;

public record ExamDto(
    Guid Id,
    string Name,
    DateOnly ExamDate,
    string? Description,
    DateTime CreatedAt,
    string CreatedByName,
    string? RubricFileName = null,
    string? RubricFileContentType = null,
    /// <summary>پاسخنامه gate — when true the grader receives the examiner's
    /// model-answer pages as the reference. Default false; flipped per exam
    /// only after the M4 golden-set gate passes.</summary>
    bool GroundTruthGradingEnabled = false
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