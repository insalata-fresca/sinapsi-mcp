using ApprovalBridge.Broker.Model;
using Sinapsi.Nats.EventPlane;
using Xunit;

namespace ApprovalBridge.Broker.Tests;

/// <summary>
/// Deny-by-default dispatch (docs/66 §3 (5)). The broker dispatches an approved action through the C2
/// <see cref="IActCommandDispatcher"/> seam wired to <see cref="NullActCommandDispatcher"/>: the executor
/// (E1.4) is unbuilt, so every dispatch is REJECTED and nothing acts. An approval still succeeds as a
/// coordination decision, but its terminal <c>executed</c> fact records that nothing ran.
/// </summary>
public sealed class DenyByDefaultDispatchTests
{
    private const string Requester = "agent:worker/session-9";
    private const string Operator = "operator:stefano";

    [Fact]
    public async Task ApprovedAction_IsNotExecuted_TheNullDispatcherRejects()
    {
        var h = BrokerFixture.Build(new NullActCommandDispatcher()); // the real deny-by-default seam
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        var o = await h.Broker.ApproveAsync(req.RequestId, Operator);

        Assert.True(o.Accepted);              // the one-shot CAS won...
        Assert.True(o.Dispatched);            // ...and reached the seam...
        Assert.False(o.ExecutorAccepted);     // ...which REJECTED it — nothing executed.
        Assert.Equal(NullActCommandDispatcher.RejectReason, o.Detail);
    }

    [Fact]
    public async Task ExecutedFact_CarriesNoOkStatus_WhenDispatchWasDenied()
    {
        var h = BrokerFixture.Build(new NullActCommandDispatcher());
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);
        await h.Broker.ApproveAsync(req.RequestId, Operator);

        var executed = h.Emitter.Facts.Single(f => f.Verdict == "executed");
        // result_status is null (not "ok") because deny-by-default meant nothing acted.
        Assert.Null(executed.Envelope["result_status"]);
        Assert.Contains("deny-by-default", executed.Envelope["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task DispatchedCommand_UsesApprovalExecuteKind_TargetsTheTargetHost_AndCarriesNoSecret()
    {
        var dispatcher = new RecordingDispatcher(ActCommandAck.Reject(NullActCommandDispatcher.RejectReason));
        var h = BrokerFixture.Build(dispatcher);
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);
        await h.Broker.ApproveAsync(req.RequestId, Operator);

        var cmd = Assert.Single(dispatcher.Commands);
        Assert.Equal(ActCommandKind.ApprovalBridgeExecute, cmd.Kind);
        Assert.Equal("delivery.command.approval-execute", cmd.Subject); // act-command tree, not a fact
        Assert.Equal("ct199-garmin", cmd.Target);                       // the TARGET host, not the requester
        Assert.Equal(req.RequestId, cmd.CorrelationId);
        // The command carries only action coordination — no auth_code, no secret material.
        var blob = $"{cmd.CommandId}{cmd.Target}{cmd.CorrelationId}{cmd.RequestedBy}{cmd.Reason}";
        Assert.DoesNotContain("abcd1234efgh", blob);
    }

    [Fact]
    public async Task UnknownAction_IsDeniedAtIntake_BeforeAnyOperatorSeesIt()
    {
        var h = BrokerFixture.Build();
        var o = await h.Broker.RequestAsync("not.registered.action", BrokerFixture.ValidParams, Requester);
        Assert.False(o.Accepted);
        Assert.Equal(BrokerRejectReason.UnknownAction, o.Reason);
        Assert.Empty(h.Emitter.Facts); // no requested fact for an unregistered action
    }
}
