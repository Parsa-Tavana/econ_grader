using EconGrader.Application.DTOs;

namespace EconGrader.Application.Interfaces;

/// <summary>
/// Internal grading client — the ONLY place that calls the Python service.
/// Nothing else may talk to the Python service directly.
/// </summary>
public interface IGradingClient
{
    Task<GradingServiceResponse> GradeAsync(
        GradingServiceRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Ask the Python service to extract all questions + rubric criteria from
    /// ONE exam-wide rubric document (absolute path on the shared storage).
    /// Saves nothing — the caller presents the result as an editable preview.
    /// </summary>
    Task<ExtractionServiceResponse> ExtractAsync(
        string absoluteFilePath,
        string fileName,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Render/normalize ONE uploaded document at ingest time (M1 pipeline):
    /// returns per-page, per-format content hashes + payloads. The caller
    /// stores pages under content-addressed keys and registers artifacts.
    /// </summary>
    Task<IngestServiceResponse> IngestAsync(
        string absoluteFilePath,
        IReadOnlyList<string>? formats = null,
        int? maxPages = null,
        int? pageOffset = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// M6 bulk split: OCR the header band of already-rendered page images.
    /// Pure tesseract OCR — no AI tokens. Returns per-page normalized student
    /// ids + confidences; grouping stays on the .NET side.
    /// </summary>
    Task<SplitHeaderResponse> SplitHeaderAsync(
        IReadOnlyList<string> absolutePagePaths,
        decimal? headerBandPct = null,
        CancellationToken cancellationToken = default
    );

    Task<TEvaluationResult?> EvaluateAsync(
        IEnumerable<(decimal TeacherScore, decimal AiScore)> runs,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<string>> GetPromptVersionsAsync(
        CancellationToken cancellationToken = default
    );
}

public record TEvaluationResult(
    int Count,
    decimal Mae,
    decimal Rmse,
    decimal ExactAgreementPct,
    decimal WithinHalfPct,
    decimal WithinOnePct,
    decimal Bias,
    decimal? PearsonR,
    decimal? QuadraticWeightedKappa
);