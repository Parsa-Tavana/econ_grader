using EconGrader.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace EconGrader.Application.Evaluation;

/// <summary>
/// Pure-C# evaluation engine — also available via Python /evaluate.
/// Computes MAE, RMSE, exact agreement, bias, Pearson, QWK.
/// </summary>
public sealed class EvaluationService
{
    private readonly IAppDbContext _db;

    public EvaluationService(IAppDbContext db) => _db = db;

    public async Task<EvaluationResultDto> ForQuestionAsync(
        Guid questionId,
        string? provider = null,
        string? modelName = null,
        CancellationToken ct = default)
    {
        var query = _db.GradingRuns
            .Where(r => r.QuestionId == questionId && r.IsValid && r.TeacherScoreSnapshot != null);

        if (provider != null) query = query.Where(r => r.Provider == provider);
        if (modelName != null) query = query.Where(r => r.ModelName == modelName);

        var runs = await query.Select(r => new
        {
            Ai = r.AiScore,
            Teacher = r.TeacherScoreSnapshot!.Value,
        }).ToListAsync(ct);

        if (!runs.Any()) return EvaluationResultDto.Empty(questionId);
        return Compute(runs.Select(r => (r.Teacher, r.Ai)).ToList(), questionId);
    }

    public async Task<EvaluationResultDto> OverallAsync(Guid examId, CancellationToken ct = default)
    {
        var questionIds = await _db.Questions
            .Where(q => q.ExamId == examId).Select(q => q.Id).ToListAsync(ct);

        var runs = await _db.GradingRuns
            .Where(r => questionIds.Contains(r.QuestionId) && r.IsValid && r.TeacherScoreSnapshot != null)
            .Select(r => new { Ai = r.AiScore, Teacher = r.TeacherScoreSnapshot!.Value })
            .ToListAsync(ct);

        if (!runs.Any()) return EvaluationResultDto.Empty(examId);
        return Compute(runs.Select(r => (r.Teacher, r.Ai)).ToList(), examId);
    }

    public static EvaluationResultDto Compute(
        IReadOnlyList<(decimal Teacher, decimal Ai)> pairs, Guid entityId)
    {
        int n = pairs.Count;
        var diffs = pairs.Select(p => (double)(p.Ai - p.Teacher)).ToArray();
        var ts = pairs.Select(p => (double)p.Teacher).ToArray();
        var ai = pairs.Select(p => (double)p.Ai).ToArray();

        double mae = diffs.Select(Math.Abs).Average();
        double rmse = Math.Sqrt(diffs.Select(d => d * d).Average());
        double exactPct = 100.0 * diffs.Count(d => Math.Abs(d) < 1e-9) / n;
        double withinHalfPct = 100.0 * diffs.Count(d => Math.Abs(d) <= 0.5) / n;
        double withinOnePct = 100.0 * diffs.Count(d => Math.Abs(d) <= 1.0) / n;
        double bias = diffs.Average();

        // Pearson
        double mts = ts.Average(), mai = ai.Average();
        double num = ts.Zip(ai, (t, a) => (t - mts) * (a - mai)).Sum();
        double d1 = Math.Sqrt(ts.Sum(t => (t - mts) * (t - mts)));
        double d2 = Math.Sqrt(ai.Sum(a => (a - mai) * (a - mai)));
        double? pearson = (d1 > 1e-9 && d2 > 1e-9) ? num / (d1 * d2) : null;

        double? qwk = ComputeQwk(ts, ai);

        var dist = new Dictionary<decimal, Dictionary<decimal, int>>();
        foreach (var (t, a) in pairs)
        {
            var tKey = Math.Round(t, 1);
            var aKey = Math.Round(a, 1);
            dist.TryAdd(tKey, new());
            dist[tKey].TryAdd(aKey, 0);
            dist[tKey][aKey]++;
        }

        return new EvaluationResultDto(entityId, n,
            Math.Round(mae, 4), Math.Round(rmse, 4),
            Math.Round(exactPct, 2), Math.Round(withinHalfPct, 2), Math.Round(withinOnePct, 2),
            Math.Round(bias, 4),
            pearson is not null ? Math.Round(pearson.Value, 4) : null,
            qwk is not null ? Math.Round(qwk.Value, 4) : null,
            dist);
    }

    private static double? ComputeQwk(double[] yTrue, double[] yPred)
    {
        try
        {
            double maxVal = Math.Max(yTrue.Max(), yPred.Max());
            double minVal = Math.Min(yTrue.Min(), yPred.Min());
            int nBins = (int)((maxVal - minVal) * 10) + 1;
            if (nBins <= 1) return 1.0;

            int ToBin(double x) => (int)Math.Round((x - minVal) * 10);
            var O = new int[nBins, nBins];
            foreach (var (t, p) in yTrue.Zip(yPred, (a, b) => (a, b)))
                O[ToBin(t), ToBin(p)]++;

            var rowSum = new int[nBins];
            var colSum = new int[nBins];
            for (int i = 0; i < nBins; i++)
                for (int j = 0; j < nBins; j++)
                {
                    rowSum[i] += O[i, j];
                    colSum[j] += O[i, j];
                }
            double Den = 0, Num = 0;
            int total = yTrue.Length;
            for (int i = 0; i < nBins; i++)
                for (int j = 0; j < nBins; j++)
                {
                    double w = Math.Pow((double)(i - j) / (nBins - 1), 2.0);
                    Num += w * O[i, j];
                    Den += w * ((double)rowSum[i] * colSum[j] / total);
                }
            if (Den == 0) return 1.0;
            return 1.0 - Num / Den;
        }
        catch { return null; }
    }
}

public record EvaluationResultDto(
    Guid EntityId,
    int Count,
    double Mae,
    double Rmse,
    double ExactAgreementPct,
    double WithinHalfPct,
    double WithinOnePct,
    double Bias,
    double? PearsonR,
    double? QuadraticWeightedKappa,
    Dictionary<decimal, Dictionary<decimal, int>> ScoreDistribution)
{
    public static EvaluationResultDto Empty(Guid id) =>
        // Mae/Rmse must be finite (0) — double.NaN fails JSON serialization and
        // turned "exam with no graded runs yet" into a 400 VALIDATION_ERROR.
        new(id, 0, 0, 0, 0, 0, 0, 0, null, null, new());
}