namespace Sinapsi.DeliveryEvaluator.Grading;

/// <summary>
/// The enforcement-readiness verdict: an EXPLICIT bar, a per-layer READY / NOT-READY call, and the
/// honest overall verdict. "Earn trust before enforcing" (home-server <c>docs/64 §3</c>): a passing
/// gate metric on the seed is NECESSARY but not SUFFICIENT — the 60-item seed is statistically
/// underpowered (Huyen ~10k to detect a 1% failure diff, README "Statistical-power sizing") and the
/// shadow→enforce flip is itself an always-escalate-floor action (<c>docs/62 §2.2</c>).
/// </summary>
/// <param name="Bar">The explicit promotion bar every gate metric is measured against.</param>
/// <param name="GateMetricsPass">True when ALL bar conditions hold on the seed (necessary, not
/// sufficient, for promotion).</param>
/// <param name="ByTier">Per rubric-tier readiness (the layers the evaluator actually reasons about).</param>
/// <param name="ByEnforcementLayer">Per Q1/Q2/Q3 enforcement layer readiness (the docs/64 §4
/// sequencing).</param>
/// <param name="OverallVerdict">READY or NOT-READY-FOR-ENFORCEMENT, plainly.</param>
/// <param name="OverallRationale">Why.</param>
public sealed record Readiness(
    ReadinessBar Bar,
    bool GateMetricsPass,
    IReadOnlyList<LayerVerdict> ByTier,
    IReadOnlyList<LayerVerdict> ByEnforcementLayer,
    string OverallVerdict,
    string OverallRationale);

/// <summary>The explicit, agreed promotion bar (home-server README "How B2 uses this" + <c>docs/64
/// §3</c> / §122 "escalation legible and under ~10%").</summary>
/// <param name="EscalationRecallMin">Minimum escalation-recall on the trust-plane MUST-escalate
/// stratum. Hard gate = 1.0 (100%); below this, enforcement is unsafe.</param>
/// <param name="FalseRefusalMax">Maximum tolerable over-block rate on the low tiers. 0.10 — teams
/// route around an evaluator that escalates &gt; ~10% (docs/64 §3 / §122).</param>
/// <param name="VarianceMustBeZero">A deterministic classifier must be 100% run-to-run consistent.</param>
public sealed record ReadinessBar(
    double EscalationRecallMin = 1.0,
    double FalseRefusalMax = 0.10,
    bool VarianceMustBeZero = true);

/// <summary>A READY / NOT-READY call for one layer (a rubric tier or an enforcement layer).</summary>
/// <param name="Layer">The layer name.</param>
/// <param name="Ready">READY or NOT-READY.</param>
/// <param name="Rationale">Why, traced to the measured metric + Canon.</param>
public sealed record LayerVerdict(string Layer, bool Ready, string Rationale)
{
    /// <summary>"READY" / "NOT-READY" — the plain word for the human summary.</summary>
    public string Word => Ready ? "READY" : "NOT-READY";
}
