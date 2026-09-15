namespace EconGrader.Domain.Entities;

/// <summary>
/// Golden-set harness run (M4): re-grades a frozen set of teacher-scored
/// answers under a candidate config (prompt/model/temperature) and diffs
/// QWK/MAE/exact-agreement against the baseline BEFORE rollout. One row per
/// harness invocation; ConfigurationJson pins exactly what was tested.
/// </summary>
public class EvalRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Frozen-set scope — answers of one question (v1). Nullable so
    /// an exam-wide or config-wide set can be modeled later.</summary>
    public Guid? QuestionId { get; set; }
    public Question? Question { get; set; }

    /// <summary>The candidate config under test: {temperature, promptVersion,
    /// runs, note}. The baseline comparison re-uses the same frozen answer set.</summary>
    public string ConfigurationJson { get; set; } = "{}";

    /// <summary>Baseline metrics (computed from PERSISTED runs of the same
    /// answers before the harness re-grades) — frozen at invocation time.</summary>
    public decimal? BaselineQwk { get; set; }
    public decimal? BaselineMae { get; set; }
    public decimal? BaselineExactAgreementPct { get; set; }
    public int BaselineCount { get; set; }

    /// <summary>Candidate metrics (fresh runs produced by this harness run).</summary>
    public decimal? CandidateQwk { get; set; }
    public decimal? CandidateMae { get; set; }
    public decimal? CandidateExactAgreementPct { get; set; }
    public int CandidateCount { get; set; }

    /// <summary>QWK change (candidate − baseline). Negative beyond the gate
    /// (default −0.05) marks the run Regressed=true — do NOT roll out.</summary>
    public decimal? QwkDelta { get; set; }
    public bool Regressed { get; set; }

    /// <summary>Per-answer harness results: [{answerId, baselineScore(s),
    /// candidateScore(s), delta}].</summary>
    public string? DetailsJson { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
