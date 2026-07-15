using ApprovalBridge.Broker.Model;
using Sinapsi.Nats.EventPlane;
using Xunit;

namespace ApprovalBridge.Broker.Tests;

/// <summary>
/// Expiry (docs/66 §5.2) and operator reject. A request past its one-shot window can never be approved;
/// a rejected request is terminal and safe (never dispatches).
/// </summary>
public sealed class ExpiryAndRejectTests
{
    private const string Requester = "agent:worker/session-5";
    private const string Operator = "operator:stefano";

    [Fact]
    public async Task ExpiredRequest_CannotBeApproved()
    {
        var dispatcher = new RecordingDispatcher(ActCommandAck.Accept());
        var h = BrokerFixture.Build(dispatcher, expirySeconds: 300);
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        h.Clock.Advance(TimeSpan.FromSeconds(301)); // past the window
        var o = await h.Broker.ApproveAsync(req.RequestId, Operator);

        Assert.False(o.Accepted);
        Assert.Equal(BrokerRejectReason.Expired, o.Reason);
        Assert.Empty(dispatcher.Commands);
    }

    [Fact]
    public async Task Reaper_TransitionsDuePending_ToExpired_AndEmitsExpired()
    {
        var h = BrokerFixture.Build(expirySeconds: 300);
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        h.Clock.Advance(TimeSpan.FromSeconds(301));
        var expired = await h.Broker.ExpireDueAsync();

        Assert.Equal(1, expired);
        Assert.Equal(RequestStatus.Expired, (await h.Store.GetAsync(req.RequestId))!.Value.Status);
        Assert.Contains("expired", h.Emitter.Verdicts);
    }

    [Fact]
    public async Task Reject_TerminatesPending_AndNeverDispatches()
    {
        var dispatcher = new RecordingDispatcher(ActCommandAck.Accept());
        var h = BrokerFixture.Build(dispatcher);
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        var rej = await h.Broker.RejectAsync(req.RequestId, Operator);
        Assert.True(rej.Accepted);
        Assert.Equal(RequestStatus.Rejected, (await h.Store.GetAsync(req.RequestId))!.Value.Status);

        // A rejected request can no longer be approved.
        var late = await h.Broker.ApproveAsync(req.RequestId, Operator);
        Assert.False(late.Accepted);
        Assert.Equal(BrokerRejectReason.NotPending, late.Reason);
        Assert.Empty(dispatcher.Commands);
    }
}
