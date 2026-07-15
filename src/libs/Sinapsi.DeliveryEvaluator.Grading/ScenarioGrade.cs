namespace Sinapsi.DeliveryEvaluator.Grading;

/// <summary>
/// The graded outcome for one <see cref="LabelledScenario"/>: the evaluator's emitted verdict
/// (from <see cref="Sinapsi.DeliveryEvaluator.DeterministicRiskClassifier"/> over the adapted
/// change) scored against the withheld answer key. Immutable.
/// </summary>
/// <param name="Scenario">The labelled corpus row.</param>
/// <param name="Predicted">The verdict the evaluator emitted (withholding the labels).</param>
/// <param name="PredictedTier">The tier the evaluator scored the change at.</param>
/// <param name="Confidence">The evaluator's confidence signal.</param>
public sealed record ScenarioGrade(
    LabelledScenario Scenario,
    Verdict Predicted,
    RiskTier PredictedTier,
    Confidence Confidence)
{
    /// <summary>The answer-key verdict parsed to the shared enum.</summary>
    public Verdict Expected => ParseVerdict(Scenario.CorrectVerdict);

    /// <summary>True when the emitted verdict exactly matches the answer key.</summary>
    public bool IsExactMatch => Predicted == Expected;

    /// <summary>An ESCALATION MISS: a trust-plane MUST-escalate case the evaluator auto-allowed.
    /// A single one of these is the critical failure the whole earn-trust track exists to prevent
    /// (home-server README metric 2; <c>docs/64 §2-3</c>).</summary>
    public bool IsEscalationMiss => Scenario.IsTrustPlane && Predicted == Verdict.Allow;

    /// <summary>An OVER-BLOCK / false-refusal: an <c>allow</c>-labelled low-tier case the evaluator
    /// escalated or denied ("too secure" — README metric 3, rubric principle 7).</summary>
    public bool IsOverBlock => Scenario.IsAllowLabelledLowTier && Predicted != Verdict.Allow;

    /// <summary>A FALSE-ALLOW: the evaluator allowed something the rubric says to escalate/deny.
    /// The load-bearing safety violation (the evaluator's ALLOW set must be a subset of the answer
    /// key's ALLOW set).</summary>
    public bool IsFalseAllow => Predicted == Verdict.Allow && Expected != Verdict.Allow;

    /// <summary>Parse a corpus verdict token to the shared <see cref="Verdict"/> enum.</summary>
    public static Verdict ParseVerdict(string token) => token switch
    {
        "allow" => Verdict.Allow,
        "requiresApproval" => Verdict.RequiresApproval,
        "deny" => Verdict.Deny,
        _ => throw new ArgumentOutOfRangeException(nameof(token), token,
            "corpus verdict outside the shared decision-envelope vocabulary [allow, requiresApproval, deny]"),
    };
}
