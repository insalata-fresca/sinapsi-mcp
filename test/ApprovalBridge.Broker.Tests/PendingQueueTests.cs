using ApprovalBridge.Broker.Model;
using ApprovalBridge.Broker.Registry;
using Xunit;

namespace ApprovalBridge.Broker.Tests;

/// <summary>
/// The READ-ONLY pending-queue projection (E1.7, docs/66 §6 step 3): joins the store's pending
/// entries with the registry so the Console can render <c>title</c> + typed params + provenance
/// (requester identity, action_id, expiry) — never the requester's free-text rationale, which this
/// model never carries in the first place. Listing must never itself be a state transition or an
/// enforcement point; those stay exclusively in <see cref="Core.BridgeBroker.ApproveAsync"/> /
/// <see cref="Core.BridgeBroker.RejectAsync"/>.
/// </summary>
public sealed class PendingQueueTests
{
    private const string Requester = "agent:worker/session-7";
    private const string Operator = "operator:stefano";

    [Fact]
    public async Task PendingRequest_IsListed_WithTitleTypedParamsAndProvenance()
    {
        var h = BrokerFixture.Build();
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        var view = Assert.Single(await h.Broker.ListPendingAsync());
        Assert.Equal(req.RequestId, view.RequestId);
        Assert.Equal(BrokerFixture.DemoActionId, view.ActionId);
        Assert.Equal("Garmin OAuth code→token exchange", view.Title);      // the registry title, never agent prose
        Assert.Equal(Requester, view.RequesterIdentity);
        Assert.Equal("yellow", view.RiskTier);
        Assert.NotNull(view.Params);
        Assert.Equal("abcd1234efgh", view.Params!["auth_code"]!.GetValue<string>());   // the TYPED params, not a digest
    }

    [Fact]
    public async Task ConsumedRequest_IsNoLongerListed()
    {
        var h = BrokerFixture.Build();
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);
        await h.Broker.ApproveAsync(req.RequestId, Operator);

        Assert.Empty(await h.Broker.ListPendingAsync());
    }

    [Fact]
    public async Task RejectedRequest_IsNoLongerListed()
    {
        var h = BrokerFixture.Build();
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);
        await h.Broker.RejectAsync(req.RequestId, Operator);

        Assert.Empty(await h.Broker.ListPendingAsync());
    }

    [Fact]
    public async Task ExpiredRequest_IsNoLongerListed()
    {
        var h = BrokerFixture.Build(expirySeconds: 60);
        await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        await h.Broker.ExpireDueAsync();

        Assert.Empty(await h.Broker.ListPendingAsync());
    }

    [Fact]
    public async Task DeregisteredAction_IsDroppedFromTheList_FailClosed()
    {
        // Two actions registered at request time; simulate de-registration by rebuilding the broker
        // over the SAME store but a registry that no longer carries the requested action.
        var h = BrokerFixture.Build();
        await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        var emptyRegistry = new InMemoryActionRegistry([]);
        var broker2 = new Core.BridgeBroker(
            emptyRegistry, h.Store, h.Emitter, h.Dispatcher, new Core.InMemoryRateLimiter(), h.Clock, new Core.CryptoNonceSource());

        Assert.Empty(await broker2.ListPendingAsync());
    }

    [Fact]
    public async Task MultiplePending_AreOrderedBySoonestExpiryFirst()
    {
        var h = BrokerFixture.Build();
        var early = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, "agent:a");
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        var later = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, "agent:b");

        var views = await h.Broker.ListPendingAsync();
        Assert.Equal(2, views.Count);
        Assert.Equal(early.RequestId, views[0].RequestId);   // requested first ⇒ expires first
        Assert.Equal(later.RequestId, views[1].RequestId);
    }
}
