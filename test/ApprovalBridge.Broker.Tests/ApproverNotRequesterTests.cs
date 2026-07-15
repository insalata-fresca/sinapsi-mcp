using ApprovalBridge.Broker.Model;
using Sinapsi.Nats.EventPlane;
using Xunit;

namespace ApprovalBridge.Broker.Tests;

/// <summary>
/// I7 / T1 — the requesting agent can NEVER approve its own request. The broker enforces
/// <c>approver_identity != requester_identity</c> structurally and refuses self-approval, independent of
/// the (deferred, E1.5) approve-channel authz. A self-approval consumes nothing and dispatches nothing.
/// </summary>
public sealed class ApproverNotRequesterTests
{
    private const string Requester = "agent:worker/session-7";

    [Fact]
    public async Task SelfApproval_ByTheRequestingIdentity_IsRejected()
    {
        var dispatcher = new RecordingDispatcher(ActCommandAck.Accept());
        var h = BrokerFixture.Build(dispatcher);
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        var o = await h.Broker.ApproveAsync(req.RequestId, approverIdentity: Requester);

        Assert.False(o.Accepted);
        Assert.Equal(BrokerRejectReason.SelfApproval, o.Reason);
        Assert.False(o.Dispatched);
        Assert.Empty(dispatcher.Commands);                          // nothing dispatched
        Assert.Equal(RequestStatus.Pending, (await h.Store.GetAsync(req.RequestId))!.Value.Status); // not consumed
    }

    [Fact]
    public async Task EmptyApprover_IsRejected_NoAnonymousApproval()
    {
        var h = BrokerFixture.Build();
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        var o = await h.Broker.ApproveAsync(req.RequestId, approverIdentity: "");

        Assert.False(o.Accepted);
        Assert.Equal(BrokerRejectReason.SelfApproval, o.Reason);
    }

    [Fact]
    public async Task DistinctOperator_CanApprove()
    {
        var h = BrokerFixture.Build(new RecordingDispatcher(ActCommandAck.Accept()));
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        var o = await h.Broker.ApproveAsync(req.RequestId, approverIdentity: "operator:stefano");

        Assert.True(o.Accepted);
    }

    [Fact]
    public async Task SelfApprovalCheck_PrecedesConsume_SoRetryByRealOperatorStillWorks()
    {
        var h = BrokerFixture.Build(new RecordingDispatcher(ActCommandAck.Accept()));
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        await h.Broker.ApproveAsync(req.RequestId, approverIdentity: Requester);       // self-approval: refused, no consume
        var real = await h.Broker.ApproveAsync(req.RequestId, approverIdentity: "operator:stefano");

        Assert.True(real.Accepted); // the failed self-approval did not burn the one-shot
    }
}
