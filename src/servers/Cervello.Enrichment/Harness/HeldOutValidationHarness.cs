using Cervello.Enrichment.Math;
using Cervello.Enrichment.Policy;

namespace Cervello.Enrichment.Harness;

/// <summary>
/// The held-out VALIDATION harness + the graded-auto-apply gate (spec <c>voiceprint-store</c> →
/// "Auto-apply stays gated until validated"; tasks E5 4.4/4.6; the deferred design Q7 acceptance
/// bar). It measures TPR/FPR of a CANDIDATE threshold on a HELD-OUT labeled set (distinct from the
/// re-fit set) and decides whether graded auto-apply may be enabled: the fitted threshold must meet
/// the operator's acceptance bar (min TPR AND max FPR).
///
/// <para>The gate is the phase switch: PASS → the engine may run <see cref="PolicyPhase.GradedAutoApply"/>;
/// FAIL → it stays <see cref="PolicyPhase.EscalateOnly"/> (every attribution an open-point),
/// regardless of the 0.62 default. The bar is a PARAMETER (<see cref="AcceptanceBar"/>), set with
/// the operator; proven now on synthetic held-out data.</para>
/// </summary>
public static class HeldOutValidationHarness
{
    /// <summary>
    /// Validate a candidate threshold on a held-out labeled set and decide the graded-auto-apply gate.
    /// </summary>
    public static ValidationReport Validate(
        double candidateThreshold, IReadOnlyList<LabeledSample> heldOut, AcceptanceBar bar)
    {
        ArgumentNullException.ThrowIfNull(heldOut);
        ArgumentNullException.ThrowIfNull(bar);
        if (heldOut.Count < 2)
            throw new ArgumentException("held-out validation needs at least two samples", nameof(heldOut));

        var same = new List<double>();
        var diff = new List<double>();
        for (var i = 0; i < heldOut.Count; i++)
        for (var j = i + 1; j < heldOut.Count; j++)
        {
            var cos = Cosine.Similarity(heldOut[i].Embedding, heldOut[j].Embedding);
            if (string.Equals(heldOut[i].Person, heldOut[j].Person, StringComparison.Ordinal)) same.Add(cos);
            else diff.Add(cos);
        }
        if (same.Count == 0 || diff.Count == 0)
            throw new ArgumentException("held-out set must contain both same- and different-speaker pairs");

        var tp = same.Count(c => c >= candidateThreshold);
        var fp = diff.Count(c => c >= candidateThreshold);
        var tpr = (double)tp / same.Count;
        var fpr = (double)fp / diff.Count;

        var passed = tpr >= bar.MinTpr && fpr <= bar.MaxFpr;
        var phase = passed ? PolicyPhase.GradedAutoApply : PolicyPhase.EscalateOnly;
        var reason = passed
            ? $"held-out TPR {tpr:0.###} ≥ {bar.MinTpr:0.###} and FPR {fpr:0.###} ≤ {bar.MaxFpr:0.###} — graded auto-apply may be enabled"
            : $"held-out TPR {tpr:0.###} (min {bar.MinTpr:0.###}) / FPR {fpr:0.###} (max {bar.MaxFpr:0.###}) — gate stays escalate-only";

        return new ValidationReport(candidateThreshold, tpr, fpr, same.Count, diff.Count, passed, phase, bar, reason);
    }
}

/// <summary>
/// The operator's acceptance bar for enabling graded auto-apply (design Q7). The candidate
/// threshold must, on the held-out set, hit at least <see cref="MinTpr"/> true positives AND at most
/// <see cref="MaxFpr"/> false positives.
/// </summary>
public sealed record AcceptanceBar
{
    public AcceptanceBar(double minTpr, double maxFpr)
    {
        if (minTpr is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(minTpr), minTpr, "minTpr in (0,1]");
        if (maxFpr is < 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(maxFpr), maxFpr, "maxFpr in [0,1)");
        MinTpr = minTpr;
        MaxFpr = maxFpr;
    }

    public double MinTpr { get; }
    public double MaxFpr { get; }

    /// <summary>A conservative default bar (TPR ≥ 0.95, FPR ≤ 0.02) — the operator overrides this.</summary>
    public static AcceptanceBar Default { get; } = new(0.95, 0.02);
}

/// <summary>The held-out validation result + the gate decision (the phase the engine should run).</summary>
public sealed record ValidationReport(
    double CandidateThreshold,
    double Tpr,
    double Fpr,
    int SameCount,
    int DiffCount,
    bool Passed,
    PolicyPhase RecommendedPhase,
    AcceptanceBar Bar,
    string Reason);
