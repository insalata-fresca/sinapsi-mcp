using Sinapsi.Governance.Events;
using Sinapsi.Governance.Slo;
using Xunit;

namespace Sinapsi.Governance.Tests;

/// <summary>
/// The escalation-rate SLO alerting (docs/64 §3): two-sided — alert on over-escalation
/// ("Overwhelming HITL") AND on suspiciously-low escalation (rubber-stamping), with a
/// min-sample guard. This is the SLO-alerting test the D1 DoD calls out.
/// </summary>
public sealed class EscalationSloTests
{
    private static EscalationSlo NewSlo(IGovernanceEventSink? sink = null) =>
        new(EscalationSloConfig.Default, clock: () => DateTimeOffset.UnixEpoch, sink: sink);

    [Fact]
    public void WithinBand_IsHealthy_NoAlert()
    {
        var report = NewSlo().Evaluate(escalated: 5, total: 100); // 5%
        Assert.Equal(EscalationSloStatus.Healthy, report.Status);
        Assert.False(report.ShouldAlert);
        Assert.Equal(0.05, report.Rate, 3);
    }

    [Fact]
    public void AboveUpperThreshold_BreachesHigh_OverwhelmingHitl()
    {
        var report = NewSlo().Evaluate(escalated: 25, total: 100); // 25% > 10%
        Assert.Equal(EscalationSloStatus.BreachHigh, report.Status);
        Assert.True(report.ShouldAlert);
    }

    [Fact]
    public void BelowLowerThreshold_BreachesLow_RubberStamping()
    {
        var report = NewSlo().Evaluate(escalated: 0, total: 500); // 0% < 0.5%
        Assert.Equal(EscalationSloStatus.BreachLow, report.Status);
        Assert.True(report.ShouldAlert);
    }

    [Fact]
    public void BelowMinSample_IsInsufficientData_NoAlert()
    {
        var report = NewSlo().Evaluate(escalated: 0, total: 5); // 0% but only 5 decisions
        Assert.Equal(EscalationSloStatus.InsufficientData, report.Status);
        Assert.False(report.ShouldAlert); // must NOT false-alarm on a tiny window
    }

    [Fact]
    public void JustOverTenPercent_Breaches_BoundaryIsExclusive()
    {
        var report = NewSlo().Evaluate(escalated: 11, total: 100); // 11% > 10%
        Assert.Equal(EscalationSloStatus.BreachHigh, report.Status);

        var exactly = NewSlo().Evaluate(escalated: 10, total: 100); // 10% == upper → healthy (not >)
        Assert.Equal(EscalationSloStatus.Healthy, exactly.Status);
    }

    [Fact]
    public void EvaluatesOverADecisionWindow()
    {
        var window = Enumerable.Repeat(DeliveryDecisionKind.AutoProceeded, 90)
            .Concat(Enumerable.Repeat(DeliveryDecisionKind.Escalated, 10)) // 10% escalated
            .ToList();
        var report = NewSlo().Evaluate(window);
        Assert.Equal(10, report.Escalated);
        Assert.Equal(100, report.Total);
        Assert.Equal(EscalationSloStatus.Healthy, report.Status);
    }

    [Fact]
    public void Evaluation_EmitsAnSloFact()
    {
        var sink = new RecordingGovernanceEventSink();
        NewSlo(sink).Evaluate(escalated: 30, total: 100);
        var ev = Assert.Single(sink.Events);
        Assert.Equal("slo", ev.Kind);
        Assert.Equal(GovernanceChannels.Slo(EscalationSloStatus.BreachHigh.ToString()), ev.Subject);
    }

    [Fact]
    public void RejectsImpossibleCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewSlo().Evaluate(escalated: 5, total: 3));
    }
}
