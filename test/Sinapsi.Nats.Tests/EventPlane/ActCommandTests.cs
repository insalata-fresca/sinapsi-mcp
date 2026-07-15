using Sinapsi.Nats.EventPlane;
using Xunit;

namespace Sinapsi.Nats.Tests.EventPlane;

/// <summary>
/// The COMMAND half of the split (docs/64 §3): a command is addressed, rejectable, and lands on
/// the act-command tree — never a verdict-fact subject. The default dispatcher is deny-by-default.
/// </summary>
public sealed class ActCommandTests
{
    private static ActCommand Merge(string corr = "req-1") =>
        new("cmd-1", ActCommandKind.MergePullRequest, "ste/sinapsi-mcp#123", corr, "mission-control", "CI green");

    [Fact]
    public void Command_Subject_IsUnderTheActCommandRoot_NotAFact()
    {
        var c = Merge();
        Assert.Equal("delivery.command.merge-pr", c.Subject);
        Assert.True(EventPlaneChannels.IsActCommandSubject(c.Subject));
        Assert.False(EventPlaneChannels.IsVerdictFactSubject(c.Subject));
    }

    [Fact]
    public void Deploy_Subject_HasItsOwnSlug()
        => Assert.Equal("delivery.command.deploy",
            new ActCommand("c", ActCommandKind.Deploy, "svc", "r", "who", "why").Subject);

    [Fact]
    public void Ack_IsRejectable_WithReason()
    {
        var rej = ActCommandAck.Reject("target already merged");
        Assert.False(rej.Accepted);
        Assert.Equal(ActCommandDisposition.Rejected, rej.Disposition);
        Assert.Equal("target already merged", rej.Reason);

        Assert.True(ActCommandAck.Accept().Accepted);
    }

    [Fact]
    public async Task NullDispatcher_RejectsEveryCommand_DenyByDefaultAtTheActSeam()
    {
        // While the act-path executor is unbuilt, a verdict must never silently cause an act:
        // the safe default REJECTS.
        var ack = await new NullActCommandDispatcher().DispatchAsync(Merge());
        Assert.False(ack.Accepted);
        Assert.Equal(NullActCommandDispatcher.RejectReason, ack.Reason);
    }

    [Fact]
    public void CorrelationId_IsCarried_ToJoinVerdictToAct()
        => Assert.Equal("req-abc", Merge("req-abc").CorrelationId);

    [Fact]
    public void Dispatcher_IsTheContract_ActPathImplementsIt()
        => Assert.True(typeof(IActCommandDispatcher).IsAssignableFrom(typeof(NullActCommandDispatcher)));
}
