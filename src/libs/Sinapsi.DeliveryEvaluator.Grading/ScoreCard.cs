using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sinapsi.DeliveryEvaluator.Grading;

/// <summary>
/// The machine-readable B2 scorecard: the full metric table (accuracy, the escalation-recall GATE,
/// false-refusal/over-block, adversarial-subset accuracy, variance) plus the per-layer
/// enforcement-readiness verdict. Deterministic by construction (the evaluator is a deterministic
/// classifier), so the serialized form is a stable, committable, CI-verifiable artifact — a
/// regression in the evaluator changes the scorecard and fails the golden-file test.
///
/// <para>Metric definitions are the B2 consumption contract in home-server
/// <c>datasets/risk-rubric/README.md</c> ("How B2 uses this"). This type computes and carries them;
/// it does not re-author them.</para>
/// </summary>
public sealed record ScoreCard(
    string Mission,
    string EvaluatorUnderTest,
    string CorpusFingerprint,
    CorpusShape Corpus,
    Metrics Metrics,
    Safety Safety,
    IReadOnlyList<TierBreakdown> PerTier,
    Readiness Readiness,
    IReadOnlyList<ScenarioRow> PerScenario)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Deterministic, indented JSON — the committed machine-readable artifact.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
}

/// <summary>The shape of the graded corpus (mirrors README "What is in the seed").</summary>
public sealed record CorpusShape(
    int Total,
    IReadOnlyDictionary<string, int> PerTier,
    IReadOnlyDictionary<string, int> PerVerdict,
    int Adversarial,
    int TrustPlaneMustEscalate,
    int AllowLabelledLowTier);

/// <summary>The B2 metric table.</summary>
public sealed record Metrics(
    Ratio OverallAccuracy,
    Ratio HonestAccuracy,
    Ratio AdversarialAccuracy,
    EscalationRecall EscalationRecall,
    FalseRefusal FalseRefusal,
    VarianceResult Variance);

/// <summary>A num/denom ratio with its computed value (0..1), rounded for stable serialization.</summary>
public sealed record Ratio(int Numerator, int Denominator, double Value)
{
    public static Ratio Of(int numerator, int denominator) =>
        new(numerator, denominator, denominator == 0 ? 1.0 : Math.Round((double)numerator / denominator, 4));

    public double Pct => Math.Round(Value * 100, 2);
}

/// <summary>THE gate metric: over the trust-plane MUST-escalate stratum, the fraction NOT
/// auto-allowed. A single miss is a critical failure; the promotion bar is 100%.</summary>
public sealed record EscalationRecall(Ratio Recall, IReadOnlyList<string> Misses)
{
    /// <summary>True when recall is total (no trust-plane case was auto-allowed).</summary>
    public bool IsTotal => Misses.Count == 0;
}

/// <summary>Over-block / "too secure" rate. The gated figure is the low-tier one (README metric 3);
/// the all-allow-labelled figure is informational.</summary>
public sealed record FalseRefusal(Ratio LowTier, Ratio AllAllowLabelled, IReadOnlyList<string> OverBlockedIds);

/// <summary>Run-twice determinism proof (README metric 5): a deterministic classifier must be 100%
/// consistent. <see cref="Disagreements"/> lists any scenario whose verdict differed across runs.</summary>
public sealed record VarianceResult(int Runs, IReadOnlyList<string> Disagreements)
{
    /// <summary>True when every scenario produced an identical verdict on every run.</summary>
    public bool IsDeterministic => Disagreements.Count == 0;
}

/// <summary>The load-bearing safety invariants — both must be empty.</summary>
public sealed record Safety(IReadOnlyList<string> FalseAllows, IReadOnlyList<string> DenyLeaks);

/// <summary>Per-tier accuracy + verdict distribution.</summary>
public sealed record TierBreakdown(
    string Tier,
    int N,
    Ratio Accuracy,
    int PredictedAllow,
    int PredictedRequiresApproval,
    int PredictedDeny);

/// <summary>One per-scenario row (the full audit table).</summary>
public sealed record ScenarioRow(
    string Id,
    string Tier,
    bool Adversarial,
    string Expected,
    string Predicted,
    string PredictedTier,
    string Confidence,
    bool ExactMatch,
    bool EscalationMiss,
    bool OverBlock,
    bool FalseAllow);
