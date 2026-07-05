// ---------------------------------------------------------------------------
// IndexerCapabilitiesTests - pins the composition decision Program.cs acts on
// (indexer-generalization, docs/architecture/indexer-generalization.md):
//   (a) all-unset ⇒ current bundled behaviour (index+search.mcp+search.http+
//       learn_publish=on, shared-bus consumer) — the image is a no-op when a
//       profile/role has not yet started emitting the new keys.
//   (b) learn_publish=false ⇒ LearnTools is ABSENT from the MCP tool-type list
//       (not merely unreachable) and NeedsAnyNatsConnection reflects that no
//       learn identity is required when nothing else needs the bus either.
//   (c) nats.mode=isolated ⇒ the index worker shape is TimerOnly and
//       NeedsAnyNatsConnection is false when learn_publish is also off — i.e.
//       the process is provably NATS-connection-free, while search + index
//       still compose (search.mcp/search.http unaffected by NATS mode).
//
// This tests the DECISION layer directly (IndexerCapabilities), not the full
// ASP.NET Core pipeline — Program.cs contains no branching logic beyond
// "read caps, act on it", so pinning the decision here is the load-bearing,
// hermetic proof (no Postgres/NATS/ONNX needed).
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using Sinapsi.Indexer;
using Xunit;

namespace Sinapsi.Indexer.Tests;

public sealed class IndexerCapabilitiesTests
{
    // ── (a) all-unset defaults reproduce today's bundled behaviour ───────────

    [Fact]
    public void AllUnset_ReproducesTodaysBundledBehaviour()
    {
        var caps = new IndexerCapabilities(index: true, searchMcp: true, searchHttp: true, learnPublish: true, natsIsolated: false);

        Assert.Equal(IndexWorkerShape.SharedBusConsumer, caps.WorkerShape);
        Assert.True(caps.NeedsAnyNatsConnection);
        Assert.Equal(new[] { typeof(IndexTools), typeof(LearnTools) }, caps.McpToolTypes());
    }

    [Fact]
    public void FromEnvironment_WithNoCapVarsSet_MatchesBundledDefaults()
    {
        foreach (var v in new[]
                 {
                     "INDEXER_CAP_INDEX", "INDEXER_CAP_SEARCH_MCP", "INDEXER_CAP_SEARCH_HTTP",
                     "INDEXER_CAP_LEARN_PUBLISH", "INDEXER_NATS_MODE",
                 })
            Environment.SetEnvironmentVariable(v, null);

        var caps = IndexerCapabilities.FromEnvironment();

        Assert.True(caps.Index);
        Assert.True(caps.SearchMcp);
        Assert.True(caps.SearchHttp);
        Assert.True(caps.LearnPublish);
        Assert.False(caps.NatsIsolated);
        Assert.Equal(IndexWorkerShape.SharedBusConsumer, caps.WorkerShape);
        Assert.True(caps.NeedsAnyNatsConnection);
        Assert.Equal(2, caps.McpToolTypes().Count);
    }

    // ── (b) learn_publish=false ⇒ no publish_learning tool, no learn identity ─

    [Fact]
    public void LearnPublishDisabled_ExcludesLearnToolsFromMcpSurface()
    {
        var caps = new IndexerCapabilities(index: true, searchMcp: true, searchHttp: true, learnPublish: false, natsIsolated: false);

        var types = caps.McpToolTypes();
        Assert.DoesNotContain(typeof(LearnTools), types);
        Assert.Contains(typeof(IndexTools), types); // search.mcp is independent — still present
        Assert.Single(types);
    }

    [Fact]
    public void LearnPublishDisabled_AloneWithIsolatedNats_NeedsNoNatsConnectionAtAll()
    {
        // index off, nats isolated, learn_publish off ⇒ absolutely nothing needs
        // a NATS connection: the mechanical "zero shared-bus reach" guarantee.
        var caps = new IndexerCapabilities(index: false, searchMcp: true, searchHttp: true, learnPublish: false, natsIsolated: true);

        Assert.False(caps.NeedsAnyNatsConnection);
        Assert.Equal(IndexWorkerShape.None, caps.WorkerShape);
    }

    // ── (c) nats.mode=isolated ⇒ TimerOnly shape; search + index still compose ─

    [Fact]
    public void NatsIsolated_WithIndexOn_SelectsTimerOnlyShape_NotSharedBusConsumer()
    {
        var caps = new IndexerCapabilities(index: true, searchMcp: true, searchHttp: true, learnPublish: false, natsIsolated: true);

        Assert.Equal(IndexWorkerShape.TimerOnly, caps.WorkerShape);
        Assert.NotEqual(IndexWorkerShape.SharedBusConsumer, caps.WorkerShape);
    }

    [Fact]
    public void NatsIsolated_IndexAndSearchCapabilities_StillComposed()
    {
        // Isolated mode changes HOW index reaches freshness (timer, not push),
        // not WHETHER search/index compose — cervello profile shape.
        var caps = new IndexerCapabilities(index: true, searchMcp: true, searchHttp: true, learnPublish: false, natsIsolated: true);

        Assert.True(caps.Index);
        Assert.True(caps.SearchMcp);
        Assert.True(caps.SearchHttp);
        Assert.NotEqual(IndexWorkerShape.None, caps.WorkerShape);
        Assert.Contains(typeof(IndexTools), caps.McpToolTypes());
    }

    [Fact]
    public void NatsIsolated_PlusLearnPublishOn_StillNeedsANatsConnection_ForTheLearnIdentityOnly()
    {
        // Isolated mode is an index-freshness knob; if the profile ALSO enables
        // learn_publish, that capability still needs its own (scoped, publish-
        // only) NATS identity independent of the index worker's shape. This is
        // an unusual/unexpected combination for a bus-isolated tenant (cervello
        // profile sets learn_publish=false, DESIGN doc §3.4) but the composition
        // helper must not silently paper over it — it correctly reports "yes, a
        // NATS connection is still needed" so a reviewer/operator catches a
        // profile that isolates the index but leaves publish on.
        var caps = new IndexerCapabilities(index: true, searchMcp: true, searchHttp: true, learnPublish: true, natsIsolated: true);

        Assert.Equal(IndexWorkerShape.TimerOnly, caps.WorkerShape); // index itself opens no NATS connection
        Assert.True(caps.NeedsAnyNatsConnection); // but learn-publish still does
    }

    // ── index disabled entirely ────────────────────────────────────────────────

    [Fact]
    public void IndexDisabled_SelectsNoWorkerShape_RegardlessOfNatsMode()
    {
        var sharedBus = new IndexerCapabilities(index: false, searchMcp: true, searchHttp: true, learnPublish: false, natsIsolated: false);
        var isolated = new IndexerCapabilities(index: false, searchMcp: true, searchHttp: true, learnPublish: false, natsIsolated: true);

        Assert.Equal(IndexWorkerShape.None, sharedBus.WorkerShape);
        Assert.Equal(IndexWorkerShape.None, isolated.WorkerShape);
    }

    // ── search sub-toggles are independent of each other and of index/learn ──

    [Fact]
    public void SearchMcpAndSearchHttp_AreIndependentToggles()
    {
        var mcpOnly = new IndexerCapabilities(index: true, searchMcp: true, searchHttp: false, learnPublish: false, natsIsolated: false);
        Assert.Contains(typeof(IndexTools), mcpOnly.McpToolTypes());
        Assert.False(mcpOnly.SearchHttp);

        var httpOnly = new IndexerCapabilities(index: true, searchMcp: false, searchHttp: true, learnPublish: false, natsIsolated: false);
        Assert.DoesNotContain(typeof(IndexTools), httpOnly.McpToolTypes());
        Assert.True(httpOnly.SearchHttp);
    }

    // ── everything off ─────────────────────────────────────────────────────────

    [Fact]
    public void AllCapabilitiesDisabled_EmptyToolListAndNoWorkerAndNoNatsConnection()
    {
        var caps = new IndexerCapabilities(index: false, searchMcp: false, searchHttp: false, learnPublish: false, natsIsolated: true);

        Assert.Empty(caps.McpToolTypes());
        Assert.Equal(IndexWorkerShape.None, caps.WorkerShape);
        Assert.False(caps.NeedsAnyNatsConnection);
        Assert.False(caps.SearchHttp);
    }

    // ── (d) nats.mode=private (cervello-private-events.md OPTION-1) ──────────────

    [Fact]
    public void NatsPrivate_WithIndexOn_SelectsPrivateSubjectConsumerShape()
    {
        var caps = new IndexerCapabilities(
            index: true, searchMcp: true, searchHttp: true, learnPublish: false, natsMode: IndexerNatsMode.Private);

        Assert.Equal(IndexWorkerShape.PrivateSubjectConsumer, caps.WorkerShape);
        Assert.NotEqual(IndexWorkerShape.SharedBusConsumer, caps.WorkerShape);
        Assert.NotEqual(IndexWorkerShape.TimerOnly, caps.WorkerShape);
    }

    [Fact]
    public void NatsPrivate_NeedsANatsConnection_ForTheScopedIndexerIdentity()
    {
        var caps = new IndexerCapabilities(
            index: true, searchMcp: true, searchHttp: true, learnPublish: false, natsMode: IndexerNatsMode.Private);

        Assert.True(caps.NeedsAnyNatsConnection);
    }

    [Fact]
    public void NatsPrivate_IsNeitherLegacyIsolatedNorSharedBus()
    {
        var caps = new IndexerCapabilities(
            index: true, searchMcp: true, searchHttp: true, learnPublish: false, natsMode: IndexerNatsMode.Private);

        // Back-compat bool must not misreport private as isolated.
        Assert.False(caps.NatsIsolated);
        Assert.Equal(IndexerNatsMode.Private, caps.NatsMode);
    }

    // FromEnvironment_WithNatsModePrivate_SelectsPrivateSubjectConsumer lives in
    // IndexerConfigTests (the [Collection("indexer-env")] class) instead of here —
    // this class is NOT collection-guarded (see FromEnvironment_WithNoCapVarsSet_
    // MatchesBundledDefaults above, a pre-existing env-mutating test in this same
    // un-guarded class), so a test here that sets process env can race against
    // IndexerConfigTests's env-mutating tests under parallel xUnit execution.
    // Keeping the env-mutating FromEnvironment(private) proof inside the already
    // serialized indexer-env collection avoids adding a NEW instance of that
    // pre-existing race rather than fixing it.

    // ── back-compat: the bool-natsIsolated constructor still cannot express private ──

    [Fact]
    public void BoolConstructor_NatsIsolatedTrue_IsIsolated_NotPrivate()
    {
        var caps = new IndexerCapabilities(index: true, searchMcp: true, searchHttp: true, learnPublish: false, natsIsolated: true);

        Assert.Equal(IndexerNatsMode.Isolated, caps.NatsMode);
        Assert.Equal(IndexWorkerShape.TimerOnly, caps.WorkerShape);
    }
}
