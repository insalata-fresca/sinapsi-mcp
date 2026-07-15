namespace Sinapsi.Governance.Slo;

/// <summary>
/// The escalation-rate SLO band. <see cref="UpperThreshold"/> is the "Overwhelming HITL"
/// ceiling (~10%); <see cref="LowerThreshold"/> is the rubber-stamping floor;
/// <see cref="MinSample"/> is the smallest window over which a rate is trustworthy (below
/// it the status is <see cref="EscalationSloStatus.InsufficientData"/>, so a handful of
/// decisions can't false-alarm).
/// </summary>
public sealed record EscalationSloConfig(double UpperThreshold, double LowerThreshold, int MinSample)
{
    /// <summary>
    /// Canon defaults: alert above 10% (docs/64 §3 "escalation legible and under ~10%") and
    /// below 0.5% (a near-zero escalation rate over a real window is the rubber-stamp smell),
    /// requiring at least 20 decisions before judging.
    /// </summary>
    public static EscalationSloConfig Default { get; } = new(UpperThreshold: 0.10, LowerThreshold: 0.005, MinSample: 20);
}
