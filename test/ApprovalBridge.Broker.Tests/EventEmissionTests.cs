using ApprovalBridge.Broker.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Sinapsi.Nats.EventPlane;
using Xunit;

namespace ApprovalBridge.Broker.Tests;

/// <summary>
/// I4 — every step is a typed FACT on <c>homelab.security.approval.&lt;action_id&gt;.&lt;verdict&gt;</c>, joined by
/// <c>correlation_id == request_id</c>; unclassifiable → the C2 <see cref="DeadLetterRouter"/>
/// (deny-by-default, never silent-drop). (docs/66 §9, §6.)
/// </summary>
public sealed class EventEmissionTests
{
    private const string Requester = "agent:worker/session-4";
    private const string Operator = "operator:stefano";

    [Fact]
    public async Task FullChain_EmitsRequestedApprovedExecuted_AllJoinedByCorrelationId()
    {
        var h = BrokerFixture.Build(new NullActCommandDispatcher());
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);
        await h.Broker.ApproveAsync(req.RequestId, Operator);

        Assert.Equal(new[] { "requested", "approved", "executed" }, h.Emitter.Verdicts.ToArray());
        Assert.All(h.Emitter.Facts, f => Assert.Equal(req.RequestId, f.CorrelationId));
        Assert.All(h.Emitter.Facts, f => Assert.Equal(req.RequestId, f.Envelope["correlation_id"]!.GetValue<string>()));
    }

    [Fact]
    public void SubjectFor_IsUnderTheApprovalFactRoot()
    {
        var subject = BridgeEnvelope.SubjectFor(BrokerFixture.DemoActionId, "approved");
        Assert.Equal("homelab.security.approval.garmin.oauth.exchange.approved", subject);
    }

    [Fact]
    public void Envelope_HasBridgeLayerAndOperatorApprovalQuestion()
    {
        var env = BridgeEnvelope.Build("a.b", "requested", "ct1 (id)", "agent:x", "", "why", "digest", null, "corr-1");
        Assert.Equal("bridge", env["layer"]!.GetValue<string>());
        Assert.Equal("operator-approval", env["question"]!.GetValue<string>());
        Assert.Equal("approval-bridge", env["surface"]!.GetValue<string>());
    }

    [Fact]
    public async Task ApprovedFact_CarriesTheApprover_RequestedFactDoesNot()
    {
        var h = BrokerFixture.Build(new NullActCommandDispatcher());
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);
        await h.Broker.ApproveAsync(req.RequestId, Operator);

        Assert.Equal("", h.Emitter.Facts.First(f => f.Verdict == "requested").Envelope["approver"]!.GetValue<string>());
        Assert.Equal(Operator, h.Emitter.Facts.First(f => f.Verdict == "approved").Envelope["approver"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnclassifiableVerdict_IsDeadLettered_DenyByDefault_NeverEmitted()
    {
        var sink = new RecordingDeadLetterSink();
        var emitter = new LoggingApprovalEventEmitter(NullLogger<LoggingApprovalEventEmitter>.Instance, sink);
        var envelope = BridgeEnvelope.Build("a.b", "bogus-verdict", "t", "r", "", "why", "digest", null, "corr-42");

        await emitter.EmitAsync(new ApprovalFact("a.b", "bogus-verdict", envelope, "corr-42"));

        var (outcome, changeRef) = Assert.Single(sink.Writes);
        Assert.Equal("corr-42", changeRef);
        Assert.Equal("deny", outcome.Verdict);                          // non-permissive fallback
        Assert.StartsWith(EventPlaneChannels.DeadLetterSubjectRoot, outcome.DlqSubject);
    }
}
