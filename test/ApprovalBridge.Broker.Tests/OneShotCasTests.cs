using ApprovalBridge.Broker.Model;
using Sinapsi.Nats.EventPlane;
using Xunit;

namespace ApprovalBridge.Broker.Tests;

/// <summary>
/// I3 / T3 — ONE-SHOT. One approval → exactly one execution of exactly one named action. Enforced
/// server-side by nonce + short expiry + an ATOMIC CAS <c>pending→consumed</c> performed BEFORE dispatch,
/// not by trusting the operator/agent to be disciplined (docs/66 §5).
/// </summary>
public sealed class OneShotCasTests
{
    private const string Requester = "agent:worker/session-1";
    private const string Operator = "operator:stefano";

    [Fact]
    public async Task SecondApproval_ForSameRequest_IsRejected_AfterConsume()
    {
        var h = BrokerFixture.Build(new RecordingDispatcher(ActCommandAck.Accept()));
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        var first = await h.Broker.ApproveAsync(req.RequestId, Operator);
        var second = await h.Broker.ApproveAsync(req.RequestId, Operator);

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        // The replay finds the request already consumed — the CAS is the gate, not trust.
        Assert.Equal(BrokerRejectReason.NotPending, second.Reason);
    }

    [Fact]
    public async Task Consume_HappensBeforeDispatch_AndDispatchesExactlyOnce()
    {
        var dispatcher = new RecordingDispatcher(ActCommandAck.Accept());
        var h = BrokerFixture.Build(dispatcher);
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        await h.Broker.ApproveAsync(req.RequestId, Operator);
        await h.Broker.ApproveAsync(req.RequestId, Operator); // replay

        // Exactly one dispatch — the second approval never reached the executor seam.
        Assert.Single(dispatcher.Commands);
        var stored = await h.Store.GetAsync(req.RequestId);
        Assert.Equal(RequestStatus.Consumed, stored!.Value.Status);
    }

    [Fact]
    public async Task ConcurrentApprovals_ExactlyOneWins_TheCasIsAtomic()
    {
        var dispatcher = new RecordingDispatcher(ActCommandAck.Accept());
        var h = BrokerFixture.Build(dispatcher);
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        // 24 racing approvals of the SAME request_id — replay/concurrency storm.
        var results = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(_ => h.Broker.ApproveAsync(req.RequestId, Operator)));

        Assert.Equal(1, results.Count(r => r.Accepted));          // exactly one CAS wins
        Assert.Single(dispatcher.Commands);                        // exactly one execution attempted
        Assert.All(results.Where(r => !r.Accepted),
            r => Assert.Contains(r.Reason, new[] { BrokerRejectReason.CasLost, BrokerRejectReason.NotPending }));
    }

    [Fact]
    public async Task ForgedNonce_ThatDoesNotMatchServerHeld_IsRejected()
    {
        var h = BrokerFixture.Build();
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        var o = await h.Broker.ApproveAsync(req.RequestId, Operator, presentedNonce: "forged-nonce-value");

        Assert.False(o.Accepted);
        Assert.Equal(BrokerRejectReason.NonceMismatch, o.Reason);
        // Still pending — a forged approval consumes nothing.
        Assert.Equal(RequestStatus.Pending, (await h.Store.GetAsync(req.RequestId))!.Value.Status);
    }

    [Fact]
    public async Task UnknownRequestId_IsRejected()
    {
        var h = BrokerFixture.Build();
        var o = await h.Broker.ApproveAsync("does-not-exist", Operator);
        Assert.False(o.Accepted);
        Assert.Equal(BrokerRejectReason.UnknownRequest, o.Reason);
    }
}
