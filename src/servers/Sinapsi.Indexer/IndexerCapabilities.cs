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

/// <summary>The three-way <c>INDEXER_NATS_MODE</c> switch
/// (docs/architecture/cervello-private-events.md, OPTION-1). Back-compat:
/// <c>SharedBus</c> is the default (unset ⇒ today's bundled behaviour).</summary>
public enum IndexerNatsMode
{
    /// <summary>Default. Admin/consumer identity, <c>HOMELAB_AUDIT</c>, <c>homelab.git.&gt;</c>.</summary>
    SharedBus,
    /// <summary>No NATS connection at all — timer-only re-scan.</summary>
    Isolated,
    /// <summary>Event-driven on a scoped nkey + a PRIVATE subject tree/stream —
    /// structurally barred from shared subjects (S50 invariant 3). The cervello
    /// profile (cervello-private-events.md).</summary>
    Private,
}

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
    /// <summary>index=true, nats.mode=private: the SAME <see cref="IndexerWorker"/>
    /// NATS-consuming shape as <see cref="SharedBusConsumer"/> (identical FetchAsync
    /// engine), but connected with a SCOPED nkey to a PRIVATE subject/stream — never
    /// <c>HOMELAB_AUDIT</c> / <c>homelab.git.&gt;</c>. Distinguished from
    /// <see cref="SharedBusConsumer"/> only so Program.cs can apply the private-mode
    /// fail-closed subject/stream validation (belt-and-braces config layer) before
    /// constructing the worker.</summary>
    PrivateSubjectConsumer,
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

    /// <summary>Back-compat: true for <see cref="IndexerNatsMode.Isolated"/> only.
    /// Prefer <see cref="NatsMode"/> for the full three-way switch.</summary>
    public bool NatsIsolated => NatsMode == IndexerNatsMode.Isolated;

    /// <summary>The full three-way <c>INDEXER_NATS_MODE</c> switch (shared-bus |
    /// isolated | private). cervello-private-events.md OPTION-1.</summary>
    public IndexerNatsMode NatsMode { get; }

    public IndexerCapabilities(bool index, bool searchMcp, bool searchHttp, bool learnPublish, bool natsIsolated)
        : this(index, searchMcp, searchHttp, learnPublish,
               natsIsolated ? IndexerNatsMode.Isolated : IndexerNatsMode.SharedBus)
    {
    }

    /// <summary>Full constructor taking the three-way <see cref="IndexerNatsMode"/>
    /// directly (private-mode support). The bool-<c>natsIsolated</c> overload above
    /// is kept for existing call sites (isolated/shared-bus only; cannot express
    /// private) — it forwards here.</summary>
    public IndexerCapabilities(bool index, bool searchMcp, bool searchHttp, bool learnPublish, IndexerNatsMode natsMode)
    {
        Index = index;
        SearchMcp = searchMcp;
        SearchHttp = searchHttp;
        LearnPublish = learnPublish;
        NatsMode = natsMode;
    }

    /// <summary>Read the composition from the process environment (via
    /// <see cref="IndexerConfig"/>'s fail-closed readers).</summary>
    public static IndexerCapabilities FromEnvironment() => new(
        index: IndexerConfig.CapIndex(),
        searchMcp: IndexerConfig.CapSearchMcp(),
        searchHttp: IndexerConfig.CapSearchHttp(),
        learnPublish: IndexerConfig.CapLearnPublish(),
        natsMode: IndexerConfig.NatsMode());

    /// <summary>Which index-worker shape (if any) to register. Never more than one
    /// type when <see cref="Index"/> is true.</summary>
    public IndexWorkerShape WorkerShape =>
        !Index ? IndexWorkerShape.None
        : NatsMode switch
        {
            IndexerNatsMode.Isolated => IndexWorkerShape.TimerOnly,
            IndexerNatsMode.Private => IndexWorkerShape.PrivateSubjectConsumer,
            _ => IndexWorkerShape.SharedBusConsumer,
        };

    /// <summary>True when ANY capability needs a live NATS connection at all
    /// (the admin read/consume identity for a shared-bus index consumer, the
    /// scoped cervello-indexer identity for a private-mode consumer, or the
    /// scoped publish-only identity for learn-publish). False means the process
    /// must open NO NATS connection whatsoever — the mechanical guarantee behind
    /// S50 invariant 3 for an isolated, search-only tenant.</summary>
    public bool NeedsAnyNatsConnection =>
        WorkerShape is IndexWorkerShape.SharedBusConsumer or IndexWorkerShape.PrivateSubjectConsumer
        || LearnPublish;

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
