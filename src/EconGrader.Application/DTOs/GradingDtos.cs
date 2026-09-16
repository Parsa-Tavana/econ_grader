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
    DateTime CreatedAt,
    /// <summary>M1/M4 lineage — the artifacts this run consumed
    /// ([{kind, role, sha256, page?}]). Kept at the end so positional
    /// construction sites stay source-compatible.</summary>
    string? InputArtifactsJson = null)
{
    public static GradingRunSummaryDetailDto From(GradingRun r) => new(
        r.Id, r.AnswerId, r.QuestionId, r.StudentId,
        r.Provider, r.ModelName, r.ModelVersion,
        r.Temperature, r.PromptVersion, r.AiScore, r.TeacherScoreSnapshot,
        r.IsValid, r.CriteriaScoresJson, r.Reasoning,
        r.LatencyMs, r.InputTokens, r.OutputTokens, r.EstimatedCost,
        r.Error, r.CreatedAt, r.InputArtifactsJson);
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

/// <summary>
/// Mirrors the Python /ingest response — one page, one format at a time.
/// Every property needs an explicit attribute (short keys like "id" pattern);
/// sha256 is the hash of the payload's own bytes (content address).
/// </summary>
public record IngestServiceResponse(
    [property: JsonPropertyName("pages")] IReadOnlyList<IngestPage> Pages,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("total_pages")] int TotalPages,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings
);

public record IngestPage(
    [property: JsonPropertyName("page_number")] int PageNumber,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("formats")] IReadOnlyList<IngestPageFormat> Formats
);

public record IngestPageFormat(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("data_b64")] string DataB64
);

/// <summary>
/// Mirrors the Python /split-header response (M6 bulk split). Per-page OCR
/// facts only — grouping lives on the .NET side. Explicit short-key
/// attributes like every other Python-facing record here.
/// </summary>
public record SplitHeaderResponse(
    [property: JsonPropertyName("pages")] IReadOnlyList<SplitHeaderPage> Pages,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings
);

public record SplitHeaderPage(
    [property: JsonPropertyName("page_number")] int PageNumber,
    [property: JsonPropertyName("raw_id")] string? RawId,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("ocr_text")] string OcrText
);
/// <summary>
/// Queue-mode job view (M2). CamelCase JSON via the API's global policy —
/// no snake_case attributes needed (internal contract, not the Python one).
/// </summary>
public record GradingJobDto(
    Guid Id,
    Guid AnswerId,
    decimal Temperature,
    string PromptVersion,
    int EnsembleIndex,
    string Status,
    int Attempts,
    string? ErrorKind,
    string? Error,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    Guid? GradingRunId,
    DateTime CreatedAt)
{
    public static GradingJobDto From(GradingJob j) => new(
        j.Id, j.AnswerId, j.Temperature, j.PromptVersion, j.EnsembleIndex,
        j.Status, j.Attempts, j.ErrorKind, j.Error,
        j.StartedAt, j.FinishedAt, j.GradingRunId, j.CreatedAt);
}
