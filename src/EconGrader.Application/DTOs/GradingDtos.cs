namespace EconGrader.Application.DTOs;

/// <summary>
/// Mirrors the Python grading service's GradeResponse — the contract between
/// .NET and the internal FastAPI microservice.
/// </summary>
public record GradingServiceResponse(
    string RunId,
    string Provider,
    string ModelName,
    string? ModelVersion,
    string PromptVersion,
    decimal Temperature,
    decimal AiScore,
    string Reasoning,
    IReadOnlyList<GradingCriterionScore> CriteriaScores,
    decimal? Confidence,
    IReadOnlyList<string> FlaggedAmbiguities,
    bool IsValid,
    IReadOnlyList<string> ValidationErrors,
    string RawResponse,
    int InputTokens,
    int OutputTokens,
    long LatencyMs,
    decimal EstimatedCostUsd,
    string? Error
);

public record GradingCriterionScore(
    string CriterionId,
    decimal Score,
    decimal MaxScore,
    string? Comment
);

/// <summary>Request payload sent to the Python /grade endpoint.</summary>
public record GradingServiceRequest(
    string StudentId,
    string QuestionId,
    string QuestionText,
    GradingRubricDto Rubric,
    IReadOnlyList<string> AnswerImagePaths,
    IReadOnlyList<string> QuestionImagePaths,
    decimal MaxScore,
    decimal Temperature,
    string PromptVersion,
    string? Provider
);

public record GradingRubricDto(IReadOnlyList<GradingCriterionDto> Criteria);

public record GradingCriterionDto(string Id, string Description, decimal MaxScore);