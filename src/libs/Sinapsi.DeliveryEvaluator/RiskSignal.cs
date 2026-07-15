namespace Sinapsi.DeliveryEvaluator;

/// <summary>What a detected <see cref="RiskSignal"/> does to the verdict.</summary>
public enum SignalEffect
{
    /// <summary>Always forces <see cref="Verdict.Deny"/> regardless of tier — a
    /// <c>docs/62 §2</c> always-escalate-floor / <c>docs/61 §7.4</c> welded-shut item
    /// (shadow→enforce flip, credential force/overwrite, floor-weakening, security-control
    /// disarm, hard-coded credential, <c>wg0</c>, god-mode-required).</summary>
    HardFloorDeny,

    /// <summary>A trust-plane surface was touched: promotes the tier to
    /// <see cref="RiskTier.TrustPlane"/>, on which no agent-cleared <see cref="Verdict.Allow"/>
    /// exists (<c>docs/65</c> principle 5).</summary>
    TrustPlaneEscalate,

    /// <summary>A non-trust reason the change must escalate at its own tier (infra disruptive /
    /// firewall / subnet / admin-DNS / snapshot-mandated; app with no PR-time gate / cross-service
    /// blast radius). Forces at least <see cref="Verdict.RequiresApproval"/>.</summary>
    Escalate,

    /// <summary>A concrete positive clearance for its <see cref="RiskSignal.ImpliedTier"/>
    /// (docs-clean, a default-off literal, a green deterministic CI gate, an additive/
    /// verified-safe infra change). Necessary — never sufficient — for <see cref="Verdict.Allow"/>.</summary>
    AllowPositive,

    /// <summary>Contradicts an allow criterion (a flag that is actually on / reached by a cron /
    /// has no confirmable default). Blocks <see cref="Verdict.Allow"/> →
    /// <see cref="Verdict.RequiresApproval"/> (<c>docs/65</c> principle 1 — effect over
    /// declaration).</summary>
    Contradiction,
}

/// <summary>
/// One deterministic observation about a change — the atom the classifier reasons over. Detected by
/// path/content rules (<see cref="PathTierClassifier"/> / <see cref="ValueSignatureScanner"/>),
/// NEVER by an LLM and NEVER from the untrusted PR title/body. Immutable.
/// </summary>
/// <param name="Code">Stable machine id (e.g. <c>shadow-enforce-flip</c>, <c>openfga-tuple</c>).</param>
/// <param name="Effect">What it does to the verdict.</param>
/// <param name="ImpliedTier">The tier this signal implies for the tier=max computation.</param>
/// <param name="Description">Human-readable, operator-facing reason.</param>
/// <param name="Surface">The trust surface it names, when applicable (for the escalation envelope).</param>
public sealed record RiskSignal(
    string Code,
    SignalEffect Effect,
    RiskTier ImpliedTier,
    string Description,
    TrustSurface? Surface = null);
