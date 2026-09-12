namespace EconGrader.Application.DTOs;

/// <summary>One credit component of an extracted question.</summary>
public record ExtractedCriterionDto(string CriterionId, string Description, decimal MaxScore);

/// <summary>One question as extracted by the AI from the exam-wide rubric file.</summary>
public record ExtractedQuestionDto(
    int Number,
    string Text,
    decimal MaxScore,
    IReadOnlyList<ExtractedCriterionDto> Criteria);

/// <summary>
/// Preview of an AI extraction — returned by POST /api/exams/{id}/extraction/preview
/// and SAVES NOTHING. The teacher edits it in the preview dialog, then POSTs the
/// confirmed rows to /apply.
/// </summary>
public record ExtractionPreviewDto(
    Guid ExamId,
    string? FileName,
    string? ContentType,
    IReadOnlyList<ExtractedQuestionDto> Questions,
    IReadOnlyList<string> Warnings,
    string Provider,
    string ModelName,
    int InputTokens,
    int OutputTokens,
    long LatencyMs,
    decimal EstimatedCostUsd);

/// <summary>Confirmed criterion row sent to POST /api/exams/{id}/extraction/apply.</summary>
public record ApplyExtractionCriterionDto(string CriterionId, string Description, decimal MaxScore);

/// <summary>Confirmed question row sent to POST /api/exams/{id}/extraction/apply.</summary>
public record ApplyExtractionQuestionDto(
    int Number,
    string Text,
    decimal MaxScore,
    IReadOnlyList<ApplyExtractionCriterionDto> Criteria);

public record ApplyExtractionRequest(IReadOnlyList<ApplyExtractionQuestionDto> Questions);

public record ApplyExtractionResultDto(
    int CreatedQuestions,
    int UpdatedQuestions,
    int RubricsCreated,
    int QuestionsUntouched);
