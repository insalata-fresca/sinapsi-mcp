using Sinapsi.Governance.Events;

namespace Sinapsi.Governance.Slo;

/// <summary>
/// The escalation-rate SLO — measures the fraction of delivery decisions that escalated to
/// the operator over a window, and alerts if it strays outside the healthy band
/// (docs/64 §3). Two-sided on purpose: over-escalation ("Overwhelming HITL", an attack
/// surface) AND suspiciously-low escalation (rubber-stamping) are both failures. Pure +
/// deterministic; emits an <c>slo</c> fact on evaluation.
/// </summary>
public sealed class EscalationSlo
{
    private readonly EscalationSloConfig _config;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IGovernanceEventSink _sink;

    public EscalationSlo(
        EscalationSloConfig? config = null,
        Func<DateTimeOffset>? clock = null,
        IGovernanceEventSink? sink = null)
    {
        _config = config ?? EscalationSloConfig.Default;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _sink = sink ?? NullGovernanceEventSink.Instance;
    }

    /// <summary>Evaluate the SLO from raw counts.</summary>
    public EscalationSloReport Evaluate(int escalated, int total)
    {
        if (escalated < 0 || total < 0 || escalated > total)
            throw new ArgumentOutOfRangeException(nameof(escalated),
                $"invalid counts: escalated={escalated}, total={total}");

        if (total < _config.MinSample)
        {
            return Report(EscalationSloStatus.InsufficientData, total == 0 ? 0.0 : (double)escalated / total,
                escalated, total,
                $"insufficient data: {total} decisions < MinSample {_config.MinSample}");
        }

        double rate = (double)escalated / total;
        var (status, message) = rate switch
        {
            _ when rate > _config.UpperThreshold => (EscalationSloStatus.BreachHigh,
                $"escalation rate {rate:P1} > {_config.UpperThreshold:P1} — Overwhelming HITL (over-escalating)"),
            _ when rate < _config.LowerThreshold => (EscalationSloStatus.BreachLow,
                $"escalation rate {rate:P2} < {_config.LowerThreshold:P2} — suspiciously low (rubber-stamping?)"),
            _ => (EscalationSloStatus.Healthy,
                $"escalation rate {rate:P1} within [{_config.LowerThreshold:P2}, {_config.UpperThreshold:P1}]"),
        };
        return Report(status, rate, escalated, total, message);
    }

    /// <summary>Evaluate the SLO over a window of decisions.</summary>
    public EscalationSloReport Evaluate(IEnumerable<DeliveryDecisionKind> window)
    {
        int total = 0, escalated = 0;
        foreach (var d in window)
        {
            total++;
            if (d == DeliveryDecisionKind.Escalated) escalated++;
        }
        return Evaluate(escalated, total);
    }

    private EscalationSloReport Report(EscalationSloStatus status, double rate, int escalated, int total, string message)
    {
        var report = new EscalationSloReport(status, rate, escalated, total, message);
        _sink.Emit(new GovernanceEvent(
            Subject: GovernanceChannels.Slo(status.ToString()),
            Kind: "slo",
            Summary: $"escalation-SLO {status} rate={rate:0.000} ({escalated}/{total}) — {message}",
            At: _clock()));
        return report;
    }
}
