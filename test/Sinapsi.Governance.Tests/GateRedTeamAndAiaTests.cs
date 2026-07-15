using Sinapsi.Governance;
using Sinapsi.Governance.Aia;
using Sinapsi.Governance.Events;
using Sinapsi.Governance.RedTeam;
using Xunit;

namespace Sinapsi.Governance.Tests;

public sealed class GateRedTeamAndAiaTests
{
    [Fact]
    public void RedTeam_PassesWhenGateNeverAutoAllowsAMustEscalateProbe()
    {
        var sink = new RecordingGovernanceEventSink();
        var redTeam = new GateRedTeam(sink: sink, clock: () => DateTimeOffset.UnixEpoch);

        // A correct gate: never auto-allows any of the seed probes.
        var findings = redTeam.Run(gateAutoAllows: _ => false);

        Assert.All(findings, f => Assert.True(f.Held));
        Assert.Equal(GovernanceChannels.RedTeam(passed: true), Assert.Single(sink.Events).Subject);
    }

    [Fact]
    public void RedTeam_FlagsBreach_WhenGateAutoAllowsAnInjectionProbe()
    {
        var sink = new RecordingGovernanceEventSink();
        var redTeam = new GateRedTeam(sink: sink, clock: () => DateTimeOffset.UnixEpoch);

        // A broken gate: auto-allows any trust-plane probe (the exact failure red-teaming exists to catch).
        var findings = redTeam.Run(gateAutoAllows: p => p.ChangeClass == ChangeClass.TrustPlane);

        Assert.Contains(findings, f => !f.Held);
        Assert.Equal(GovernanceChannels.RedTeam(passed: false), Assert.Single(sink.Events).Subject);
    }

    [Fact]
    public void SeedProbes_TargetTheTrustPlane()
    {
        Assert.NotEmpty(GateRedTeam.SeedProbes);
        Assert.Contains(GateRedTeam.SeedProbes, p => p.ChangeClass == ChangeClass.TrustPlane && p.MustNotAutoAllow);
    }

    [Fact]
    public void Aia_HasAStubForEveryChangeClass()
    {
        foreach (var cls in ChangeClassOrdering.All)
            Assert.Equal(cls, AiaStubLibrary.For(cls).ChangeClass);
        Assert.Equal(ChangeClassOrdering.All.Count, AiaStubLibrary.All.Count);
    }

    [Fact]
    public void Aia_TrustPlaneAndUnknown_ForbidAutomation()
    {
        Assert.False(AiaStubLibrary.For(ChangeClass.TrustPlane).AutomationPermitted);
        Assert.False(AiaStubLibrary.For(ChangeClass.Unknown).AutomationPermitted);
        // ...and lower tiers permit it (once trust is earned).
        Assert.True(AiaStubLibrary.For(ChangeClass.DocsOnly).AutomationPermitted);
        Assert.True(AiaStubLibrary.For(ChangeClass.ApplicationCode).AutomationPermitted);
    }

    [Fact]
    public void Aia_AgreesWithTheTrustLedgerCap()
    {
        // AIA "automation not permitted" ⇔ ledger "auto-proceed forbidden" for the trust plane.
        Assert.False(AiaStubLibrary.For(ChangeClass.TrustPlane).AutomationPermitted);
        Assert.True(TrustLedgerConfig.Default.IsAutoProceedForbidden(ChangeClass.TrustPlane));
    }
}
