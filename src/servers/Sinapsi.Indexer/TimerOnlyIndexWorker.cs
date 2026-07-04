// ---------------------------------------------------------------------------
// TimerOnlyIndexWorker - the "index" capability's isolated-mode shape
// (INDEXER_NATS_MODE=isolated). Runs the SAME reindex/embed engine
// (IndexerCore) as IndexerWorker, but with NO NATS client of any kind: no
// consumer, no coalesce loop (there is no push signal to coalesce), no seed,
// no nkey. Freshness is timer-bounded only (INDEXER_RESCAN_INTERVAL_MIN).
//
// This is what makes a bus-isolated tenant's "nothing reaches shared NATS"
// (S50 invariant 3) a property of the BINARY's behaviour, not merely the
// firewall/egress allowlist: this type never references NatsConnectionOptions,
// NatsConnection, or anything from Sinapsi.Nats.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sinapsi.Indexer;

/// <summary>
/// Timer-only "index" capability shape used when <c>INDEXER_NATS_MODE=isolated</c>.
/// Constructed and registered ONLY in that mode (see Program.cs) — it opens no
/// network connection to NATS, ever.
/// </summary>
public sealed class TimerOnlyIndexWorker : BackgroundService
{
    private readonly IndexerCore _core;
    private readonly ILogger<TimerOnlyIndexWorker> _log;

    public bool Ready { get; private set; }
    public bool SchemaReady => _core.SchemaReady;
    public long DocsUpserted => _core.DocsUpserted;
    public long DocsEmbedded => _core.DocsEmbedded;
    public DateTimeOffset? LastReindex => _core.LastReindex;

    public TimerOnlyIndexWorker(IIndexStore store, SourceScanner scanner, IEmbedder embedder, ILogger<TimerOnlyIndexWorker> log)
    {
        _core = new IndexerCore(store, scanner, embedder, log);
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("index capability running in ISOLATED (timer-only) mode — no NATS connection");

        await _core.EnsureSchemaWithRetryAsync(ct);

        // Initial full build, same as the shared-bus shape's startup scan.
        await _core.ReindexAllAsync(ct);
        Ready = true;

        // No coalesce loop (no push signal exists in isolated mode) — the
        // periodic safety timer is the ONLY freshness path. Embedding still runs
        // as its own decoupled background loop.
        _ = Task.Run(() => _core.EmbedLoopAsync(ct), ct);
        await _core.PeriodicRescanAsync(ct);
    }
}
