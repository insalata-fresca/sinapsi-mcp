using Sinapsi.Nats.EventPlane;
using Xunit;

namespace Sinapsi.Nats.Tests.EventPlane;

/// <summary>C3 item 5 — SYNTHETIC-monitoring-only gate before real traffic. Proves real traffic is
/// withheld until every synthetic probe passes across a satisfied bake window (docs/64 §3).</summary>
public sealed class SyntheticGateTests
{
    private static readonly SyntheticProbeResult[] AllPass =
    {
        new("smoke", true),
        new("health", true),
    };

    [Fact]
    public void AdmitsRealTraffic_WhenAllProbesPassAndBaked()
    {
        var d = SyntheticGate.Evaluate(AllPass, bakeWindowSatisfied: true);
        Assert.Equal(TrafficPhase.RealTrafficAdmitted, d.Phase);
        Assert.True(d.AdmitRealTraffic);
        Assert.Empty(d.Blockers);
    }

    [Fact]
    public void StaysSyntheticOnly_WhenAProbeFails()
    {
        var probes = new[] { new SyntheticProbeResult("smoke", true), new SyntheticProbeResult("health", false, "503") };
        var d = SyntheticGate.Evaluate(probes, bakeWindowSatisfied: true);
        Assert.Equal(TrafficPhase.SyntheticOnly, d.Phase);
        Assert.False(d.AdmitRealTraffic);
        Assert.Contains(d.Blockers, b => b.Contains("health") && b.Contains("503"));
    }

    [Fact]
    public void StaysSyntheticOnly_WhenBakeNotSatisfied()
    {
        var d = SyntheticGate.Evaluate(AllPass, bakeWindowSatisfied: false);
        Assert.False(d.AdmitRealTraffic);
        Assert.Contains(d.Blockers, b => b.Contains("bake window"));
    }

    [Fact]
    public void EmptyProbeSet_IsNotAPass()
    {
        // Admitting real traffic on zero synthetic evidence is the failure this gate blocks.
        var d = SyntheticGate.Evaluate(Array.Empty<SyntheticProbeResult>(), bakeWindowSatisfied: true);
        Assert.Equal(TrafficPhase.SyntheticOnly, d.Phase);
        Assert.False(d.AdmitRealTraffic);
        Assert.Contains(d.Blockers, b => b.Contains("no synthetic probes"));
    }

    [Fact]
    public void AllBlockersAccumulate()
    {
        var probes = new[] { new SyntheticProbeResult("smoke", false) };
        var d = SyntheticGate.Evaluate(probes, bakeWindowSatisfied: false);
        Assert.False(d.AdmitRealTraffic);
        Assert.True(d.Blockers.Count >= 2); // failed probe + unsatisfied bake
    }
}
