namespace Sinapsi.DeliveryEvaluator;

/// <summary>
/// The five risk tiers of the security-aware rubric (home-server <c>docs/65 §3</c>), ordered by
/// severity so a change can be scored at the MAXIMUM tier over every surface it touches
/// (<c>docs/65</c> principle 4 — "there is no averaging and no 'mostly safe'"). The integer
/// values encode that order; <see cref="RiskTierOrdering.Max"/> takes the most-severe.
/// </summary>
public enum RiskTier
{
    /// <summary>Fail-safe internal state: the tier could not be determined (empty/unclassifiable
    /// surfaces, no establishing signal). Not a rubric tier — it forces
    /// <see cref="Verdict.RequiresApproval"/> (<c>docs/65</c> principle 3, uncertainty → escalate).
    /// Lowest ordinal so it never wins a max-over-surfaces against a real tier.</summary>
    Unknown = 0,

    /// <summary>Only documentation/prose surfaces (<c>docs/65 §3.1</c>).</summary>
    DocsOnly = 1,

    /// <summary>A feature behind a flag that is off by default and dark as-merged
    /// (<c>docs/65 §3.2</c>).</summary>
    DefaultOffFlag = 2,

    /// <summary>Ordinary product/service code with a runtime effect that is not infra, config,
    /// or the trust plane (<c>docs/65 §3.3</c>).</summary>
    ApplicationCode = 3,

    /// <summary>Infrastructure/configuration that is not itself the trust plane
    /// (<c>docs/65 §3.4</c>).</summary>
    InfraConfig = 4,

    /// <summary>Anything touching the authorization/trust plane: OpenFGA relations, credentials,
    /// protected infra, nats/auth config, enforcement flips (<c>docs/65 §3.5</c>). This tier has
    /// NO agent-cleared <see cref="Verdict.Allow"/>.</summary>
    TrustPlane = 5,
}

/// <summary>Severity helpers for <see cref="RiskTier"/>.</summary>
public static class RiskTierOrdering
{
    /// <summary>The most-severe (highest) of two tiers — the tier = max-over-surfaces rule
    /// (<c>docs/65</c> principle 4, §4 step 3).</summary>
    public static RiskTier Max(RiskTier a, RiskTier b) => (int)a >= (int)b ? a : b;

    /// <summary>True when the tier is the trust plane, on which no agent-cleared
    /// <see cref="Verdict.Allow"/> exists (<c>docs/65</c> principle 5, §3.5).</summary>
    public static bool IsTrustPlane(this RiskTier tier) => tier == RiskTier.TrustPlane;
}
