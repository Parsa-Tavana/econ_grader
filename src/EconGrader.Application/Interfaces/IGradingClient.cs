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