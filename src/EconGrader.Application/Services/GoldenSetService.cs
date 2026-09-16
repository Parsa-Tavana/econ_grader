using EconGrader.Application.Data;
using EconGrader.Application.DTOs;
using EconGrader.Application.Exceptions;
using EconGrader.Application.Interfaces;
using EconGrader.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace EconGrader.Application.Services;

/// <summary>Golden-set harness request (M4).</summary>
public record GoldenSetRequest(
    Guid QuestionId,
    /// <summary>Candidate temperature (0.0 = deterministic).</summary>
    decimal Temperature = 0m,
    /// <summary>Candidate prompt version under test.</summary>
    string PromptVersion = "default",
    /// <summary>Max answers to re-grade (the frozen set is teacher-scored ones).</summary>
    int MaxAnswers = 10,
    /// <summary>Free-form label persisted into ConfigurationJson for the audit trail.</summary>
    string? Note = null);

/// <summary>Golden-set harness result (M4).</summary>
public record GoldenSetResultDto(
    Guid EvalRunId,
    Guid QuestionId,
    int BaselineCount,
    int CandidateCount,
    decimal? BaselineQwk,
    decimal? CandidateQwk,
    decimal? QwkDelta,
    decimal? BaselineMae,
    decimal? CandidateMae,
    decimal? BaselineExactAgreementPct,
    decimal? CandidateExactAgreementPct,
    bool Regressed,
    string Verdict);

/// <summary>
/// Golden-set evaluation harness (M4): pick N teacher-scored answers as the
/// frozen set, re-grade them under a candidate config, and diff QWK/MAE/
/// exact-agreement against the baseline (computed from the PERSISTED runs of
/// the same answers). A QWK regression beyond the gate marks the run
/// Regressed=true — evidence before any prompt/model/page-format rollout.
/// </summary>
public interface IGoldenSetService
{
    Task<GoldenSetResultDto> RunGoldenSetAsync(GoldenSetRequest request, Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<object>> ListEvalRunsAsync(Guid? questionId, CancellationToken ct = default);
}

public sealed class GoldenSetService : IGoldenSetService
{
    /// <summary>QWK drop at or beyond this delta (candidate − baseline) is a
    /// regression. Small negative drift (−0.03..−0.05) is normal run-to-run
    /// noise on a small frozen set — the gate is deliberately modest.</summary>
    public const decimal QwkRegressionGate = -0.05m;

    private static readonly JsonSerializerOptions PersistedJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IAppDbContext _db;
    private readonly IGradingOrchestrationService _orchestrator;
    private readonly IAuditLogger _audit;
    private readonly ILogger<GoldenSetService> _logger;

    public GoldenSetService(
        IAppDbContext db,
        IGradingOrchestrationService orchestrator,
        IAuditLogger audit,
        ILogger<GoldenSetService> logger)
    {
        _db = db;
        _orchestrator = orchestrator;
        _audit = audit;
        _logger = logger;
    }

    public async Task<GoldenSetResultDto> RunGoldenSetAsync(
        GoldenSetRequest request, Guid userId, CancellationToken ct = default)
    {
        if (request.MaxAnswers < 1 || request.MaxAnswers > 50)
            throw new BusinessRuleException("MaxAnswers must be between 1 and 50.", "INVALID_GOLDEN_SET_SIZE");

        // ── The frozen set: teacher-scored answers of this question ────────
        var answers = await _db.Answers
            .Where(a => a.QuestionId == request.QuestionId && a.TeacherScore != null)
            .OrderBy(a => a.Id)
            .Take(request.MaxAnswers)
            .ToListAsync(ct);
        if (answers.Count < 3)
            throw new BusinessRuleException(
                $"The golden set needs at least 3 teacher-scored answers for question {request.QuestionId} (found {answers.Count}). " +
                "Grade a batch first, set teacher scores, then re-run the harness.",
                "GOLDEN_SET_TOO_SMALL");

        // ── Baseline: persisted valid runs of the same answers ─────────────
        var answerIds = answers.Select(a => a.Id).ToList();
        var baselineRuns = await _db.GradingRuns
            .Where(r => answerIds.Contains(r.AnswerId) && r.IsValid && r.TeacherScoreSnapshot != null)
            .GroupBy(r => r.AnswerId)
            .Select(g => new { AnswerId = g.Key, Score = g.OrderBy(r => r.CreatedAt).Last().AiScore })
            .ToListAsync(ct);

        var pairs = new List<(decimal Teacher, decimal Ai)>();
        var baselineByAnswer = baselineRuns.ToDictionary(r => r.AnswerId, r => r.Score);
        foreach (var a in answers)
        {
            if (baselineByAnswer.TryGetValue(a.Id, out var ai))
                pairs.Add((a.TeacherScore!.Value, ai));
        }
        if (pairs.Count < 3)
            throw new BusinessRuleException(
                "Fewer than 3 of the frozen answers have persisted AI runs — grade them once to establish a baseline before gating.",
                "GOLDEN_SET_NO_BASELINE");

        var baseline = Evaluation.EvaluationService.Compute(
            pairs.Select(p => (p.Teacher, p.Ai)).ToList(), request.QuestionId);

        // ── Candidate: fresh runs of the SAME answers under the new config ──
        var details = new List<object>();
        var candidatePairs = new List<(decimal Teacher, decimal Ai)>();
        foreach (var answer in answers)
        {
            try
            {
                var result = await _orchestrator.GradeAnswerAsync(
                    answer.Id, request.Temperature, request.PromptVersion, ct);
                var run = result.Runs[0];
                if (run.IsValid)
                {
                    candidatePairs.Add((answer.TeacherScore!.Value, run.AiScore));
                    details.Add(new
                    {
                        answerId = answer.Id,
                        teacher = answer.TeacherScore.Value,
                        baseline = baselineByAnswer.TryGetValue(answer.Id, out var b) ? b : (decimal?)null,
                        candidate = run.AiScore,
                        delta = run.AiScore - (baselineByAnswer.TryGetValue(answer.Id, out var b2) ? b2 : run.AiScore),
                    });
                }
                else
                {
                    details.Add(new
                    {
                        answerId = answer.Id,
                        teacher = answer.TeacherScore!.Value,
                        baseline = baselineByAnswer.TryGetValue(answer.Id, out var b) ? b : (decimal?)null,
                        candidate = (decimal?)null,
                        delta = (decimal?)null,
                        error = run.Error ?? "invalid run",
                    });
                }
            }
            catch (Exception ex)
            {
                // Per-answer failure never blocks the harness — it just
                // shrinks the candidate set (with a floor, checked below).
                details.Add(new
                {
                    answerId = answer.Id,
                    teacher = answer.TeacherScore!.Value,
                    baseline = baselineByAnswer.TryGetValue(answer.Id, out var b) ? b : (decimal?)null,
                    candidate = (decimal?)null,
                    delta = (decimal?)null,
                    error = ex.Message,
                });
                _logger.LogWarning(ex, "Golden-set re-grade failed for answer {AnswerId}", answer.Id);
            }
        }
        if (candidatePairs.Count < 3)
            throw new BusinessRuleException(
                $"Only {candidatePairs.Count} of {answers.Count} candidate runs were valid — the harness needs at least 3. " +
                "Check gateway health / rubric completeness and retry.",
                "GOLDEN_SET_CANDIDATE_TOO_SMALL");

        var candidate = Evaluation.EvaluationService.Compute(
            candidatePairs.Select(p => (p.Teacher, p.Ai)).ToList(), request.QuestionId);

        // ── Diff + verdict ──────────────────────────────────────────────────
        decimal? qwkDelta = null;
        bool regressed = false;
        if (baseline.QuadraticWeightedKappa is { } baseQwk && candidate.QuadraticWeightedKappa is { } candQwk)
        {
            qwkDelta = (decimal)(candQwk - baseQwk);
            regressed = qwkDelta <= QwkRegressionGate;
        }
        var verdict = regressed
            ? $"REGRESSED — QWK dropped {Math.Abs(qwkDelta ?? 0):0.####} (gate {QwkRegressionGate}). Do not roll out this config."
            : qwkDelta is null
                ? "INCONCLUSIVE — QWK not computable on one side; inspect MAE/exact-agreement manually."
                : $"PASS — QWK {qwkDelta:+0.####;-0.####;0} vs baseline. Safe to roll out (small frozen set — re-run before trusting borderline results).";

        var evalRun = new EvalRun
        {
            QuestionId = request.QuestionId,
            ConfigurationJson = JsonSerializer.Serialize(new
            {
                request.Temperature,
                request.PromptVersion,
                request.MaxAnswers,
                request.Note,
                gate = QwkRegressionGate,
            }, PersistedJson),
            BaselineQwk = baseline.QuadraticWeightedKappa is { } bq ? (decimal)bq : null,
            BaselineMae = (decimal)baseline.Mae,
            BaselineExactAgreementPct = (decimal)baseline.ExactAgreementPct,
            BaselineCount = baseline.Count,
            CandidateQwk = candidate.QuadraticWeightedKappa is { } cq ? (decimal)cq : null,
            CandidateMae = (decimal)candidate.Mae,
            CandidateExactAgreementPct = (decimal)candidate.ExactAgreementPct,
            CandidateCount = candidate.Count,
            QwkDelta = qwkDelta,
            Regressed = regressed,
            DetailsJson = JsonSerializer.Serialize(details, PersistedJson),
            CreatedByUserId = userId,
        };
        _db.EvalRuns.Add(evalRun);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("GoldenSetEvaluated", "EvalRun", evalRun.Id, userId, new
        {
            request.QuestionId,
            request.PromptVersion,
            request.Temperature,
            baselineQwk = baseline.QuadraticWeightedKappa,
            candidateQwk = candidate.QuadraticWeightedKappa,
            evalRun.Regressed,
        });

        _logger.LogInformation(
            "Golden-set run {EvalRunId}: QWK {Baseline} → {Candidate} ({Delta}) regressed={Regressed}",
            evalRun.Id, baseline.QuadraticWeightedKappa, candidate.QuadraticWeightedKappa, qwkDelta, regressed);

        return new GoldenSetResultDto(
            evalRun.Id, request.QuestionId,
            baseline.Count, candidate.Count,
            baseline.QuadraticWeightedKappa is { } bQ ? (decimal)bQ : null,
            candidate.QuadraticWeightedKappa is { } cQ ? (decimal)cQ : null,
            qwkDelta,
            (decimal)baseline.Mae, (decimal)candidate.Mae,
            (decimal)baseline.ExactAgreementPct, (decimal)candidate.ExactAgreementPct,
            regressed, verdict);
    }

    public async Task<IReadOnlyList<object>> ListEvalRunsAsync(Guid? questionId, CancellationToken ct = default)
    {
        var query = _db.EvalRuns.AsNoTracking().AsQueryable();
        if (questionId is { } qid) query = query.Where(e => e.QuestionId == qid);
        var runs = await query
            .OrderByDescending(e => e.CreatedAt)
            .Take(100)
            .Select(e => new
            {
                e.Id,
                e.QuestionId,
                e.ConfigurationJson,
                e.BaselineQwk,
                e.BaselineMae,
                e.BaselineExactAgreementPct,
                e.BaselineCount,
                e.CandidateQwk,
                e.CandidateMae,
                e.CandidateExactAgreementPct,
                e.CandidateCount,
                e.QwkDelta,
                e.Regressed,
                e.CreatedAt,
            })
            .ToListAsync(ct);
        return runs.Cast<object>().ToList();
    }
}
