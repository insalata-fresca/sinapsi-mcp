namespace Sinapsi.Governance.Slo;

/// <summary>
/// The escalation-rate SLO verdict. The rate is legible and must sit inside a band: too
/// HIGH means "Overwhelming HITL" — an attack surface + alert fatigue (docs/64 §3); too LOW
/// means the gate is rubber-stamping (nothing ever escalates → the check has stopped
/// discriminating). Both extremes alert.
/// </summary>
public enum EscalationSloStatus
{
    /// <summary>Escalation rate inside the healthy band.</summary>
    Healthy = 0,

    /// <summary>Above the upper bound (~10%) — over-escalating; "Overwhelming HITL".</summary>
    BreachHigh = 1,

    /// <summary>Below the lower bound — suspiciously low; likely rubber-stamping.</summary>
    BreachLow = 2,

    /// <summary>Not enough decisions in the window to judge the rate; hold (do not alert).</summary>
    InsufficientData = 3,
}
