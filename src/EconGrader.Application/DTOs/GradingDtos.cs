namespace EconGrader.Application.DTOs;

using System.Text.Json.Serialization;

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
    decimal MaxScore = 0,
    decimal Temperature = 0,
    string PromptVersion = "default"
);

public record GradingRubricDto(IReadOnlyList<GradingCriterionDto> Criteria);

public record GradingCriterionDto(string Id, string Description, decimal MaxScore);

/// <summary>
/// Mirrors the Python /extract response. Uses explicit snake_case attribute
/// mappings because the Python contract uses short keys (id, max_score) that
/// the default SnakeCaseLower naming policy would mangle (criterion_id etc.).
/// EVERY property needs an attribute — the deserializer runs without a naming
/// policy, and an unbound "questions"/"error" leaves the property null and
/// turns a clean AI-failure 502 into an unhandled NullReferenceException.
/// </summary>
public record ExtractionServiceResponse(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model_name")] string ModelName,
    [property: JsonPropertyName("model_version")] string? ModelVersion,
    [property: JsonPropertyName("prompt_version")] string PromptVersion,
    [property: JsonPropertyName("questions")] IReadOnlyList<ExtractionQuestion> Questions,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("is_valid")] bool IsValid,
    [property: JsonPropertyName("validation_errors")] IReadOnlyList<string> ValidationErrors,
    [property: JsonPropertyName("raw_response")] string RawResponse,
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens,
    [property: JsonPropertyName("latency_ms")] long LatencyMs,
    [property: JsonPropertyName("estimated_cost_usd")] decimal EstimatedCostUsd,
    [property: JsonPropertyName("error")] string? Error
);

public record ExtractionQuestion(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("max_score")] decimal MaxScore,
    [property: JsonPropertyName("criteria")] IReadOnlyList<ExtractionCriterion> Criteria
);

public record ExtractionCriterion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("max_score")] decimal MaxScore
);