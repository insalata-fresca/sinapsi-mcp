namespace Sinapsi.DeliveryEvaluator;

/// <summary>
/// The three verdicts of the common decision envelope (home-server <c>docs/61 §8</c> /
/// <c>docs/65 §1</c>), shared with the Q1/Q2/Q3 authorization layers so a delivery-evaluator
/// verdict is directly comparable to an authorization verdict. The wire tokens
/// (<c>allow</c>/<c>requiresApproval</c>/<c>deny</c>) are validated against
/// <see cref="Sinapsi.Nats.EventPlane.DecisionEnvelopeContract.Verdicts"/> — the Published
/// Language — in <see cref="DeliveryVerdictEnvelope"/>.
/// </summary>
public enum Verdict
{
    /// <summary>Safe to auto-proceed on the green-light path with no operator pause
    /// (<c>docs/65 §1</c>). Emitted ONLY by an explicit positive-clearance branch — never a
    /// fall-through (fail-safe default is <see cref="RequiresApproval"/>).</summary>
    Allow,

    /// <summary>Must escalate: route to the operator/ASK gate. The fail-safe default under any
    /// uncertainty (<c>docs/64 §3</c>, <c>docs/65</c> principle 3) and the only self-clearable
    /// verdict the trust plane may reach short of <see cref="Deny"/>.</summary>
    RequiresApproval,

    /// <summary>Never-auto / hard-block. The autonomous pipeline is not an authorized actor for
    /// this change class; only a human, through the governed PAP path, may make it
    /// (<c>docs/65 §1</c>, <c>docs/61 §7.4</c> welded-shut floor).</summary>
    Deny,
}

/// <summary>Maps <see cref="Verdict"/> to/from the shared decision-envelope wire tokens.</summary>
public static class VerdictTokens
{
    /// <summary>The <c>docs/61 §8</c> wire token for a verdict.</summary>
    public static string ToToken(this Verdict verdict) => verdict switch
    {
        Verdict.Allow => "allow",
        Verdict.RequiresApproval => "requiresApproval",
        Verdict.Deny => "deny",
        _ => "requiresApproval", // fail-safe: an unmapped verdict escalates, never allows.
    };
}
