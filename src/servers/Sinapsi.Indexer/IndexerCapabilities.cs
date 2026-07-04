// ---------------------------------------------------------------------------
// IndexerCapabilities - pure composition-decision helper for Program.cs.
// Turns the four INDEXER_CAP_* flags + INDEXER_NATS_MODE into the concrete
// "what gets constructed" decisions, so the composition logic itself
// (which worker shape, which MCP tool types, whether the /search route or the
// NATS connection options get registered) is unit-testable without spinning
// up ASP.NET Core, Postgres, NATS, or ONNX.
//
// This type makes NO DI calls itself — Program.cs reads its properties and
// performs the actual builder.Services.Add*/route registration. That split is
// what lets a test assert "learn_publish=false selects zero learn identity /
// zero LearnTools" etc. as a plain, hermetic unit test.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

namespace Sinapsi.Indexer;

/// <summary>Which "index" worker shape (if any) Program.cs should register.</summary>
public enum IndexWorkerShape
{
    /// <summary>The index capability is disabled — no worker of either shape,
    /// no NATS connection for indexing, no git-pull loop.</summary>
    None,
    /// <summary>index=true, nats.mode=shared-bus (default): the NATS-consuming
    /// <see cref="IndexerWorker"/> — push-coalesced + periodic rescan.</summary>
    SharedBusConsumer,
    /// <summary>index=true, nats.mode=isolated: <see cref="TimerOnlyIndexWorker"/> —
    /// SAME reindex engine, timer-only, NO NATS connection of any kind.</summary>
    TimerOnly,
}

/// <summary>
/// Resolves the four <c>INDEXER_CAP_*</c> flags + <c>INDEXER_NATS_MODE</c> (via
/// <see cref="IndexerConfig"/>) into the concrete composition the process must
/// build. Constructed once at startup; every property is a plain bool/enum so a
/// test can assert the composition WITHOUT touching DI, ASP.NET Core, Postgres,
/// NATS, or ONNX.
/// </summary>
public sealed class IndexerCapabilities
{
    public bool Index { get; }
    public bool SearchMcp { get; }
    public bool SearchHttp { get; }
    public bool LearnPublish { get; }
    public bool NatsIsolated { get; }

    public IndexerCapabilities(bool index, bool searchMcp, bool searchHttp, bool learnPublish, bool natsIsolated)
    {
        Index = index;
        SearchMcp = searchMcp;
        SearchHttp = searchHttp;
        LearnPublish = learnPublish;
        NatsIsolated = natsIsolated;
    }

    /// <summary>Read the composition from the process environment (via
    /// <see cref="IndexerConfig"/>'s fail-closed readers).</summary>
    public static IndexerCapabilities FromEnvironment() => new(
        index: IndexerConfig.CapIndex(),
        searchMcp: IndexerConfig.CapSearchMcp(),
        searchHttp: IndexerConfig.CapSearchHttp(),
        learnPublish: IndexerConfig.CapLearnPublish(),
        natsIsolated: IndexerConfig.NatsIsolated());

    /// <summary>Which index-worker shape (if any) to register. Never both;
    /// never neither type when <see cref="Index"/> is true.</summary>
    public IndexWorkerShape WorkerShape =>
        !Index ? IndexWorkerShape.None
        : NatsIsolated ? IndexWorkerShape.TimerOnly
        : IndexWorkerShape.SharedBusConsumer;

    /// <summary>True when ANY capability needs a live NATS connection at all
    /// (the admin read/consume identity for a shared-bus index consumer, or the
    /// scoped publish-only identity for learn-publish). False means the process
    /// must open NO NATS connection whatsoever — the mechanical guarantee behind
    /// S50 invariant 3 for an isolated, search-only tenant.</summary>
    public bool NeedsAnyNatsConnection => WorkerShape == IndexWorkerShape.SharedBusConsumer || LearnPublish;

    /// <summary>The exact set of MCP tool TYPES to register. Building this list
    /// (rather than calling the compile-time generic <c>.WithTools&lt;T&gt;()</c>
    /// per type) is what lets a disabled capability contribute NOTHING to the
    /// MCP server — not merely an unreachable tool.</summary>
    public IReadOnlyList<Type> McpToolTypes()
    {
        var types = new List<Type>();
        if (SearchMcp) types.Add(typeof(IndexTools));
        if (LearnPublish) types.Add(typeof(LearnTools));
        return types;
    }
}
