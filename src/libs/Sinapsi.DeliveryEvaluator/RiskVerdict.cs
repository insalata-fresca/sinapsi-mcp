namespace Sinapsi.DeliveryEvaluator;

/// <summary>
/// The evaluator's decision on a change: the <see cref="Verdict"/>, the <see cref="RiskTier"/> it
/// was scored at (max-over-surfaces), a <see cref="Confidence"/> signal, the human reason, the
/// trust <see cref="Surfaces"/> touched (effect classification for the operator), and the raw
/// <see cref="Signals"/> that produced it. Immutable.
///
/// <para><b>Invariant (structural, asserted by tests):</b> when <see cref="Tier"/> is
/// <see cref="RiskTier.TrustPlane"/>, <see cref="Verdict"/> is never <see cref="Verdict.Allow"/>
/// (<c>docs/65</c> principle 5). And <see cref="Verdict.Allow"/> is only ever produced by an
/// explicit positive-clearance branch, never a fall-through — the default is
/// <see cref="Verdict.RequiresApproval"/> (<c>docs/64 §3</c> fail-safe default).</para>
/// </summary>
/// <param name="Verdict">allow / requiresApproval / deny.</param>
/// <param name="Tier">The tier the change was scored at.</param>
/// <param name="Confidence">Signal-strength confidence in the verdict.</param>
/// <param name="Reason">Operator-facing justification.</param>
/// <param name="Surfaces">Distinct trust surfaces the change touches (may be empty for low tiers).</param>
/// <param name="Signals">The deterministic signals behind the verdict (audit trail).</param>
/// <param name="Unparseable">True when the change could not be parsed and was dead-lettered +
/// escalated (fail-safe), rather than positively classified.</param>
public sealed record RiskVerdict(
    Verdict Verdict,
    RiskTier Tier,
    Confidence Confidence,
    string Reason,
    IReadOnlyList<TrustSurface> Surfaces,
    IReadOnlyList<RiskSignal> Signals,
    bool Unparseable = false)
{
    /// <summary>True when this change touched the trust/security plane.</summary>
    public bool TouchedTrustPlane => Tier == RiskTier.TrustPlane;
}
