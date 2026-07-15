using Sinapsi.Governance;
using Sinapsi.Governance.Events;
using Sinapsi.Nats.EventPlane;
using Xunit;

namespace Sinapsi.Governance.Tests;

/// <summary>
/// Governance signals are FACTS, never triggers — the same event-plane discipline C2/C3
/// encode (docs/64 §3). A governance subject must live under the governance fact root and
/// must never be an act-command subject.
/// </summary>
public sealed class GovernanceChannelsTests
{
    [Fact]
    public void TrustSubject_IsUnderTheGovernanceFactRoot()
    {
        var subject = GovernanceChannels.Trust(ChangeClass.TrustPlane, AutoProceedAuthority.Revoked);
        Assert.StartsWith("homelab.governance.trust.", subject);
        Assert.True(GovernanceChannels.IsGovernanceFactSubject(subject));
        GovernanceChannels.EnsureFact(subject); // does not throw
    }

    [Fact]
    public void EnsureFact_RejectsAnActCommandSubject()
    {
        // A command subject from the C2/C3 event plane must never carry a governance signal.
        var commandSubject = EventPlaneChannels.ActCommandSubjectRoot + ".merge-pr";
        Assert.Throws<ArgumentException>(() => GovernanceChannels.EnsureFact(commandSubject));
    }

    [Fact]
    public void EnsureFact_RejectsAForeignSubject()
    {
        Assert.Throws<ArgumentException>(() => GovernanceChannels.EnsureFact("some.other.tree"));
        Assert.Throws<ArgumentException>(() => GovernanceChannels.EnsureFact("  "));
    }

    [Fact]
    public void FactRoot_IsInsideTheHomelabAuditSpine()
    {
        Assert.StartsWith("homelab.", GovernanceChannels.FactSubjectRoot);
    }
}
