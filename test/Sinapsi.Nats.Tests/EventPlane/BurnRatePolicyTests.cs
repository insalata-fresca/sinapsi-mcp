using Sinapsi.Nats.EventPlane;
using Xunit;

namespace Sinapsi.Nats.Tests.EventPlane;

/// <summary>C3 item 4 — BURN-RATE as an ESCALATION TRIGGER + input to judgment, NEVER a blunt
/// auto-allow/deny (docs/64 §3, reconciling Observability gating vs Platform-Eng "optional").</summary>
public sealed class BurnRatePolicyTests
{
    private static readonly BurnRateThresholds Th = BurnRateThresholds.Default; // elevated 2×, critical 14.4×

    [Fact]
    public void IsNeverAnAllowDenyGate_RegardlessOfSeverity()
    {
        // The load-bearing invariant: no reading, in any mode, produces a gate.
        foreach (var reading in new[] { new BurnRateReading(0.5, 0.5), new BurnRateReading(3, 1), new BurnRateReading(50, 20) })
        foreach (var mode in new[] { BudgetMode.Advisory, BudgetMode.EscalationTrigger })
            Assert.False(BurnRatePolicy.Assess(reading, Th, mode).IsAllowDenyGate);
    }

    [Fact]
    public void NominalBurn_NeverEscalates()
    {
        var a = BurnRatePolicy.Assess(new BurnRateReading(1.0, 0.8), Th, BudgetMode.EscalationTrigger);
        Assert.Equal(BurnSeverity.Nominal, a.Severity);
        Assert.False(a.TriggersEscalation);
    }

    [Fact]
    public void CriticalFastBurn_TriggersEscalation_InEscalationMode()
    {
        var a = BurnRatePolicy.Assess(new BurnRateReading(20, 3), Th, BudgetMode.EscalationTrigger);
        Assert.Equal(BurnSeverity.Critical, a.Severity);
        Assert.True(a.TriggersEscalation);
        Assert.False(a.IsAllowDenyGate); // escalates — but still not a deny
    }

    [Fact]
    public void ElevatedSlowBurn_IsElevated_AndEscalatesInEscalationMode()
    {
        var a = BurnRatePolicy.Assess(new BurnRateReading(1.0, 2.5), Th, BudgetMode.EscalationTrigger);
        Assert.Equal(BurnSeverity.Elevated, a.Severity);
        Assert.True(a.TriggersEscalation);
    }

    [Fact]
    public void AdvisoryMode_NeverEscalates_EvenOnCritical()
    {
        // Platform-Eng "error budgets are optional": the signal is still surfaced (severity), but it
        // does not on its own page the floor and it never gates.
        var a = BurnRatePolicy.Assess(new BurnRateReading(50, 10), Th, BudgetMode.Advisory);
        Assert.Equal(BurnSeverity.Critical, a.Severity);
        Assert.False(a.TriggersEscalation);
        Assert.False(a.IsAllowDenyGate);
        Assert.Contains("advisory", a.Rationale);
    }

    [Fact]
    public void SeverityIsSurfaced_AsJudgmentInput_InBothModes()
    {
        // Even when it does not escalate, the severity is reported so an independent evaluator/human
        // can weigh it — that is "input to judgment".
        var advisory = BurnRatePolicy.Assess(new BurnRateReading(3, 1), Th, BudgetMode.Advisory);
        Assert.Equal(BurnSeverity.Elevated, advisory.Severity);
    }
}
