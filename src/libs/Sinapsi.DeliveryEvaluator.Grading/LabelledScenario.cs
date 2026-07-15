namespace Sinapsi.DeliveryEvaluator.Grading;

/// <summary>
/// One labelled row of the B1 golden corpus (home-server
/// <c>datasets/risk-rubric/seed-corpus.yaml</c>) — the ANSWER KEY Mission B2 grades a candidate
/// delivery evaluator against.
///
/// <para><b>The label fields are the answer key.</b> Only <see cref="DiffSummary"/> is ever fed to
/// the evaluator (via <see cref="Sinapsi.DeliveryEvaluator.CorpusScenarioAdapter"/>); the
/// <see cref="Tier"/> / <see cref="CorrectVerdict"/> / <see cref="IsAdversarial"/> fields are
/// WITHHELD at evaluation time and consulted only to score the emitted verdict (home-server
/// <c>datasets/risk-rubric/README.md</c> "How B2 uses this"). Immutable value object.</para>
/// </summary>
/// <param name="Id">Stable unique id (<c>&lt;TIER-PREFIX&gt;-&lt;NNN&gt;</c>).</param>
/// <param name="DiffSummary">What the change actually does — the effect the evaluator must infer.
/// The ONLY field fed to the evaluator.</param>
/// <param name="Tier">The rubric tier (= MAX over surfaces): docs-only | default-off-flag |
/// application-code | infra-config | trust-plane. Answer-key field.</param>
/// <param name="CorrectVerdict">The rubric-prescribed verdict: allow | requiresApproval | deny.
/// Answer-key field.</param>
/// <param name="IsAdversarial">True for a "looks-safe-but-isn't" case (declared intent contradicts
/// effect). Answer-key field.</param>
public sealed record LabelledScenario(
    string Id,
    string DiffSummary,
    string Tier,
    string CorrectVerdict,
    bool IsAdversarial)
{
    /// <summary>The trust-plane MUST-escalate stratum id — the escalation-recall denominator
    /// (home-server README metric 2, the gate).</summary>
    public const string TrustPlaneTier = "trust-plane";

    /// <summary>The three low tiers over which false-refusal / over-block is graded — escalating a
    /// trust-plane case is never over-block (README metric 3, rubric principle 7).</summary>
    public static readonly IReadOnlySet<string> LowTiers =
        new HashSet<string>(StringComparer.Ordinal) { "docs-only", "default-off-flag", "application-code" };

    /// <summary>True when this scenario sits on the trust-plane MUST-escalate stratum.</summary>
    public bool IsTrustPlane => string.Equals(Tier, TrustPlaneTier, StringComparison.Ordinal);

    /// <summary>True when this scenario is an <c>allow</c>-labelled low-tier case (the false-refusal
    /// denominator).</summary>
    public bool IsAllowLabelledLowTier =>
        string.Equals(CorrectVerdict, "allow", StringComparison.Ordinal) && LowTiers.Contains(Tier);
}
