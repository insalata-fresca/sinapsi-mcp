using Sinapsi.Nats.EventPlane;
using Xunit;

namespace Sinapsi.Nats.Tests.EventPlane;

/// <summary>
/// The verdict-FACT / act-COMMAND separation (docs/64 §3). Proves the two subject trees are
/// disjoint and that the guard refuses to let a fact become an act trigger.
/// </summary>
public sealed class EventPlaneChannelsTests
{
    [Fact]
    public void FactAndCommandRoots_AreDisjoint()
    {
        // A fact lives under homelab.>; a command lives under a top-level tree OUTSIDE it, so a
        // dedicated work-queue stream can own commands without colliding with HOMELAB_AUDIT.
        Assert.StartsWith("homelab.", EventPlaneChannels.VerdictFactSubjectRoot);
        Assert.False(EventPlaneChannels.ActCommandSubjectRoot.StartsWith("homelab.", System.StringComparison.Ordinal));
        Assert.False(EventPlaneChannels.DeadLetterSubjectRoot.StartsWith("homelab.", System.StringComparison.Ordinal));
        Assert.NotEqual(EventPlaneChannels.VerdictFactSubjectRoot, EventPlaneChannels.ActCommandSubjectRoot);
    }

    [Theory]
    [InlineData("homelab.security.authz.q2.allow.cse", true)]
    [InlineData("homelab.security.authz", true)]
    [InlineData("delivery.command.merge-pr", false)]
    [InlineData("homelab.security.authzworld", false)] // not a real child (no dot boundary)
    public void IsVerdictFactSubject_MatchesOnlyTheFactTree(string subject, bool expected)
        => Assert.Equal(expected, EventPlaneChannels.IsVerdictFactSubject(subject));

    [Theory]
    [InlineData("delivery.command.merge-pr", true)]
    [InlineData("delivery.command", true)]
    [InlineData("homelab.security.authz.q2.allow.cse", false)]
    public void IsActCommandSubject_MatchesOnlyTheCommandTree(string subject, bool expected)
        => Assert.Equal(expected, EventPlaneChannels.IsActCommandSubject(subject));

    [Fact]
    public void EnsureNotFactTriggered_Throws_OnAVerdictFactSubject()
    {
        // The canon's central invariant: the bus fact must NOT be the trigger.
        var ex = Assert.Throws<System.ArgumentException>(
            () => EventPlaneChannels.EnsureNotFactTriggered("homelab.security.authz.q2.allow.cse"));
        Assert.Contains("must never be an act trigger", ex.Message);
    }

    [Fact]
    public void EnsureNotFactTriggered_Throws_OnEmpty()
        => Assert.Throws<System.ArgumentException>(() => EventPlaneChannels.EnsureNotFactTriggered(""));

    [Fact]
    public void EnsureNotFactTriggered_Throws_WhenNotUnderCommandRoot()
        => Assert.Throws<System.ArgumentException>(() => EventPlaneChannels.EnsureNotFactTriggered("random.subject"));

    [Fact]
    public void EnsureNotFactTriggered_Passes_ForAnActCommandSubject()
    {
        var ex = Record.Exception(() => EventPlaneChannels.EnsureNotFactTriggered("delivery.command.merge-pr"));
        Assert.Null(ex);
    }
}
