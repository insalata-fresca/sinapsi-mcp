namespace Sinapsi.Governance.Slo;

/// <summary>How a delivery decision resolved — the input to the escalation-rate SLO.</summary>
public enum DeliveryDecisionKind
{
    /// <summary>The pipeline auto-proceeded on the green path (verdict allow, trust earned).</summary>
    AutoProceeded = 0,

    /// <summary>Routed to the operator (requiresApproval / deny) — an escalation. Both count
    /// toward the escalation rate: they are the human-in-the-loop load.</summary>
    Escalated = 1,
}
