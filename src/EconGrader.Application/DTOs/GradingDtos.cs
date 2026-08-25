namespace EconGrader.Application.DTOs;

/// <summary>
/// Trimmed view of a GradingRun for lists/timelines — excludes RawAiResponse
/// (often tens of KB per run) which only GET /api/grading/run/{id} returns.
/// </summary>
public record GradingRunSummaryDetailDto(
    Guid Id,
    Guid AnswerId,
    Guid QuestionId,
    Guid StudentId,
    string Provider,
    string ModelName,
    string? ModelVersion,
    decimal Temperature,
    string PromptVersion,
    decimal AiScore,
    decimal? TeacherScoreSnapshot,
    bool IsValid,
    string? CriteriaScoresJson,
    string? Reasoning,
    long LatencyMs,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCost,
    string? Error,
    DateTime CreatedAt)
{
    public static GradingRunSummaryDetailDto From(GradingRun r) => new(
        r.Id, r.AnswerId, r.QuestionId, r.StudentId,
        r.Provider, r.ModelName, r.ModelVersion,
        r.Temperature, r.PromptVersion, r.AiScore, r.TeacherScoreSnapshot,
        r.IsValid, r.CriteriaScoresJson, r.Reasoning,
        r.LatencyMs, r.InputTokens, r.OutputTokens, r.EstimatedCost,
        r.Error, r.CreatedAt);
}

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