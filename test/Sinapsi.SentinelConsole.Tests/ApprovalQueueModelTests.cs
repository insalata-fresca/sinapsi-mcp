using Sinapsi.SentinelConsole;
using Xunit;

namespace Sinapsi.SentinelConsole.Tests;

public sealed class ApprovalQueueModelTests
{
    private static ApprovalEvent E(
        string verdict, string corr, string actionId = "garmin.oauth.exchange",
        string requester = "agent:x", string approver = "", int tick = 0)
        => new(actionId, verdict, "ct199-garmin (garmin-connector)", requester, approver,
               "reason", "digest", verdict == "executed" ? "ok" : "", corr,
               new DateTimeOffset(2026, 7, 15, 0, 0, tick % 60, TimeSpan.Zero));

    [Fact]
    public void Recent_ReturnsNewestFirst_Bounded()
    {
        var aqm = new ApprovalQueueModel(capacity: 3);
        for (int i = 0; i < 5; i++)
            aqm.Record(E("requested", corr: $"req-{i}", tick: i));

        var recent = aqm.Recent(10);
        Assert.Equal(3, recent.Count);                          // capped at capacity
        Assert.Equal("req-4", recent[0].CorrelationId);         // newest first
        Assert.Equal("req-2", recent[2].CorrelationId);         // req-0, req-1 evicted
        Assert.Equal(5, aqm.Total);                             // total still counts everything seen
    }

    [Fact]
    public void Chain_JoinsByCorrelationId_OldestToNewest_FullLifecycle()
    {
        var aqm = new ApprovalQueueModel();
        aqm.Record(E("requested", corr: "req-1", requester: "agent:worker", tick: 1));
        aqm.Record(E("approved", corr: "req-1", requester: "agent:worker", approver: "operator:stefano", tick: 2));
        aqm.Record(E("executed", corr: "req-1", requester: "agent:worker", approver: "operator:stefano", tick: 3));
        aqm.Record(E("requested", corr: "other-req", tick: 2));   // a different request — must not leak in

        var chain = aqm.Chain("req-1");
        Assert.Equal(3, chain.Count);
        Assert.Equal("requested", chain[0].Verdict);   // oldest first
        Assert.Equal("approved", chain[1].Verdict);
        Assert.Equal("executed", chain[2].Verdict);
        Assert.Equal("ok", chain[2].ResultStatus);
    }

    [Fact]
    public void Chain_EmptyForUnknownOrBlank()
    {
        var aqm = new ApprovalQueueModel();
        aqm.Record(E("requested", corr: "req-1"));
        Assert.Empty(aqm.Chain("nope"));
        Assert.Empty(aqm.Chain(""));
    }

    [Fact]
    public void RejectedChain_NeverContainsExecuted()
    {
        var aqm = new ApprovalQueueModel();
        aqm.Record(E("requested", corr: "req-2", tick: 1));
        aqm.Record(E("rejected", corr: "req-2", approver: "operator:stefano", tick: 2));

        var chain = aqm.Chain("req-2");
        Assert.Equal(2, chain.Count);
        Assert.DoesNotContain(chain, e => e.Verdict == "executed");
    }
}
