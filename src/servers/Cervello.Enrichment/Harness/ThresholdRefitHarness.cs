using Cervello.Enrichment.Math;

namespace Cervello.Enrichment.Harness;

/// <summary>
/// The offline threshold RE-FIT harness (spec <c>voiceprint-store</c> → "Threshold 0.62 with a
/// mandatory re-fit plan" / "Threshold is config, re-fittable"; task E5 4.4). It productizes the
/// E0.5 methodology: from a LABELED enrollment/eval set it builds the same-speaker vs
/// different-speaker cosine distributions and recommends a threshold that maximises separation
/// under an operator TPR floor, reporting the TPR/FPR at that threshold.
///
/// <para>DETERMINISTIC and pure — no I/O, no personal audio persisted (it consumes vectors +
/// labels the caller supplies and returns aggregate stats only). Runnable later on real
/// enrollment vectors with the operator; proven now on a synthetic labeled set.</para>
///
/// <para>The recommendation is CONFIG, not code: the fitted <c>autoBand</c> feeds a
/// <c>DecisionBands</c> value with NO engine change (spec "applied by configuration").</para>
/// </summary>
public static class ThresholdRefitHarness
{
    /// <summary>
    /// Fit a threshold over a labeled set of speaker embeddings.
    /// </summary>
    /// <param name="labeledSamples">
    /// The eval set: each sample is a person label + an embedding vector. Same-label pairs are the
    /// positive (same-speaker) class; cross-label pairs are the negative (different-speaker) class.
    /// </param>
    /// <param name="targetTpr">
    /// The operator's minimum true-positive rate the fitted threshold must hold (the acceptance
    /// posture — default 0.95). The harness picks the HIGHEST threshold whose TPR ≥ this (which
    /// minimises FPR), so auto-apply is as conservative as the TPR floor allows.
    /// </param>
    public static RefitReport Fit(IReadOnlyList<LabeledSample> labeledSamples, double targetTpr = 0.95)
    {
        ArgumentNullException.ThrowIfNull(labeledSamples);
        if (labeledSamples.Count < 2)
            throw new ArgumentException("re-fit needs at least two samples", nameof(labeledSamples));
        if (targetTpr is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(targetTpr), targetTpr, "targetTpr must be in (0,1]");

        // Build the two cosine distributions over all unordered pairs.
        var same = new List<double>();
        var diff = new List<double>();
        for (var i = 0; i < labeledSamples.Count; i++)
        for (var j = i + 1; j < labeledSamples.Count; j++)
        {
            var cos = Cosine.Similarity(labeledSamples[i].Embedding, labeledSamples[j].Embedding);
            if (string.Equals(labeledSamples[i].Person, labeledSamples[j].Person, StringComparison.Ordinal))
                same.Add(cos);
            else
                diff.Add(cos);
        }
        if (same.Count == 0)
            throw new ArgumentException("re-fit needs at least one same-speaker pair (≥2 samples of one person)");
        if (diff.Count == 0)
            throw new ArgumentException("re-fit needs at least one different-speaker pair (≥2 distinct people)");

        // Candidate thresholds = the distinct cosine values (± epsilon boundaries). Evaluate each.
        var candidates = same.Concat(diff).Distinct().OrderBy(x => x).ToList();
        var evaluated = candidates
            .Select(t => Evaluate(t, same, diff))
            .ToList();

        // Recommended = the highest threshold whose TPR ≥ targetTpr (most conservative meeting the floor).
        // If none meets it, fall back to the threshold with the best Youden's J (TPR − FPR).
        var meetingFloor = evaluated.Where(e => e.Tpr >= targetTpr).ToList();
        var recommended = meetingFloor.Count > 0
            ? meetingFloor.OrderByDescending(e => e.Threshold).First()
            : evaluated.OrderByDescending(e => e.Tpr - e.Fpr).First();

        return new RefitReport(
            SameCount: same.Count,
            DiffCount: diff.Count,
            SameMean: same.Average(),
            DiffMean: diff.Average(),
            RecommendedThreshold: recommended.Threshold,
            TprAtRecommended: recommended.Tpr,
            FprAtRecommended: recommended.Fpr,
            MetTargetTpr: recommended.Tpr >= targetTpr,
            TargetTpr: targetTpr,
            Curve: evaluated);
    }

    private static ThresholdPoint Evaluate(double threshold, IReadOnlyList<double> same, IReadOnlyList<double> diff)
    {
        // A pair is called "same" when cosine ≥ threshold.
        var tp = same.Count(c => c >= threshold);
        var fp = diff.Count(c => c >= threshold);
        var tpr = (double)tp / same.Count;
        var fpr = (double)fp / diff.Count;
        return new ThresholdPoint(threshold, tpr, fpr);
    }
}

/// <summary>One labeled speaker embedding sample for the re-fit / validation harnesses (synthetic in tests).</summary>
public sealed record LabeledSample(string Person, IReadOnlyList<float> Embedding);

/// <summary>One point on the threshold sweep curve.</summary>
public sealed record ThresholdPoint(double Threshold, double Tpr, double Fpr);

/// <summary>The re-fit result: distributions + the recommended threshold + its TPR/FPR (aggregate only).</summary>
public sealed record RefitReport(
    int SameCount,
    int DiffCount,
    double SameMean,
    double DiffMean,
    double RecommendedThreshold,
    double TprAtRecommended,
    double FprAtRecommended,
    bool MetTargetTpr,
    double TargetTpr,
    IReadOnlyList<ThresholdPoint> Curve);
