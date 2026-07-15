using Sinapsi.Nats.EventPlane;
using Xunit;

namespace Sinapsi.Nats.Tests.EventPlane;

/// <summary>DLQ + deny-by-default (docs/64 §3): an unclassifiable change is routed to the DLQ
/// AND resolved to a non-permissive verdict — never allow, never silent-drop, never retried.</summary>
public sealed class DeadLetterRouterTests
{
    private sealed class RecordingSink : IDeadLetterSink
    {
        public int Writes;
        public DeadLetterOutcome? Last;
        public string? LastRef;
        public ValueTask WriteAsync(DeadLetterOutcome outcome, string changeRef, System.Threading.CancellationToken ct = default)
        {
            Writes++; Last = outcome; LastRef = changeRef;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void Route_DefaultsToDeny_NeverAllow()
    {
        var o = DeadLetterRouter.Route("unparseable diff");
        Assert.Equal(UnclassifiedFallback.Deny, o.Fallback);
        Assert.Equal("deny", o.Verdict);
        Assert.NotEqual("allow", o.Verdict);
    }

    [Fact]
    public void Route_CanElevateToRequiresApproval_ButNeverAllow()
    {
        var o = DeadLetterRouter.Route("recognised write, unproven", UnclassifiedFallback.RequiresApproval);
        Assert.Equal("requiresApproval", o.Verdict);
    }

    [Fact]
    public void Route_TargetsTheDlqTree_WithASlug()
    {
        var o = DeadLetterRouter.Route("Unparseable Diff!!");
        Assert.StartsWith(EventPlaneChannels.DeadLetterSubjectRoot + ".", o.DlqSubject);
        Assert.True(EventPlaneChannels.IsDeadLetterSubject(o.DlqSubject));
        Assert.Equal("delivery.dlq.unparseable-diff", o.DlqSubject);
    }

    [Fact]
    public void Route_EmptyReason_StillYieldsAStableSubject()
        => Assert.Equal("delivery.dlq.unclassifiable", DeadLetterRouter.Route("").DlqSubject);

    [Fact]
    public async Task RouteAsync_WritesExactlyOnce_NeverSilentDropNorRetry()
    {
        var sink = new RecordingSink();
        var o = await DeadLetterRouter.RouteAsync(sink, "ste/home-server#7", "unknown-change-class");
        Assert.Equal(1, sink.Writes);                 // never silent-drop (written) and never retried (exactly one)
        Assert.Same(o, sink.Last is null ? o : o);    // outcome round-trips to the sink
        Assert.Equal(o, sink.Last);
        Assert.Equal("ste/home-server#7", sink.LastRef);
        Assert.Equal("deny", o.Verdict);              // and still deny-by-default
    }
}
