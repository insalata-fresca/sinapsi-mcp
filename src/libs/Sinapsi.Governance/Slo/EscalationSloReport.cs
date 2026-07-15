namespace Sinapsi.Governance.Slo;

/// <summary>The computed SLO reading over a window: status, the measured rate, and the counts.</summary>
public sealed record EscalationSloReport(
    EscalationSloStatus Status,
    double Rate,
    int Escalated,
    int Total,
    string Message)
{
    /// <summary>True when the status is one that should raise an alert (either breach).</summary>
    public bool ShouldAlert => Status is EscalationSloStatus.BreachHigh or EscalationSloStatus.BreachLow;
}
