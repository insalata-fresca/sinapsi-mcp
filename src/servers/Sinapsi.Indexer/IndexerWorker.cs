using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using Sinapsi.Nats;

namespace Sinapsi.Indexer;

/// <summary>
/// Event-DRIVEN cache over source-of-truth repos (shared-bus shape —
/// <c>INDEXER_NATS_MODE=shared-bus</c>, the default). A durable consumer on a
/// JetStream stream, filtered to git-push notifications (env-driven subject,
/// default <c>events.git.&gt;</c>). A push to a watched repo only **marks that
/// repo dirty** (the event is acked immediately); a **coalescing loop** re-scans
/// each dirty repo at most once per debounce window (git pull → walk → idempotent
/// upsert → tombstone missing). This collapses a burst — including the one-time
/// backlog drain a fresh durable consumer sees — into a single re-scan instead of
/// one git-pull per historical event. A full re-scan also runs at startup and on a
/// periodic safety timer. The event log is never the source of truth — the repos
/// are.
///
/// <para>
/// The reindex/embed engine itself lives in <see cref="IndexerCore"/> (shared
/// with <see cref="TimerOnlyIndexWorker"/>, the isolated-mode shape); this class
/// owns ONLY the NATS consumer + push-coalescing behaviour on top of that engine.
/// This type is constructed and registered ONLY when the index capability is
/// enabled AND NATS mode is shared-bus (see Program.cs) — when NATS mode is
/// isolated, <see cref="TimerOnlyIndexWorker"/> runs instead and this type is
/// never constructed, so it opens no NATS connection.
/// </para>
/// </summary>
public sealed class IndexerWorker : JetStreamWorker
{
    private readonly IndexerCore _core;
    private readonly ConcurrentDictionary<string, byte> _dirty = new();
    private readonly TimeSpan _debounce;

    public bool SchemaReady => _core.SchemaReady;
    public long DocsUpserted => _core.DocsUpserted;
    public long DocsEmbedded => _core.DocsEmbedded;
    public DateTimeOffset? LastReindex => _core.LastReindex;

    public IndexerWorker(IIndexStore store, ISourceScanner scanner, IEmbedder embedder, NatsConnectionOptions opts, ILogger<IndexerWorker> log)
        : base(opts, log)
    {
        _core = new IndexerCore(store, scanner, embedder, log);
        // Fail-closed numeric config: a bad INDEXER_DEBOUNCE_SEC now throws
        // (naming the var) at startup rather than being silently clamped/defaulted.
        // A valid value behaves exactly as before.
        _debounce = TimeSpan.FromSeconds(IndexerConfig.DebounceSec());
    }

    protected override string StreamName => Environment.GetEnvironmentVariable("INDEXER_STREAM") ?? "EVENTS";
    protected override string DurableName => Environment.GetEnvironmentVariable("INDEXER_DURABLE") ?? "sinapsi-indexer";
    // Only git-push notifications drive re-indexing — NOT every event (efficiency +
    // keeps the consumer narrow). Expected subject shape: <prefix>.git.<repo>.push.<branch>.
    protected override string FilterSubject => Environment.GetEnvironmentVariable("INDEXER_WATCH_SUBJECT") ?? "events.git.>";

    protected override async ValueTask OnStartAsync(NatsJSContext js, CancellationToken ct)
    {
        await _core.EnsureSchemaWithRetryAsync(ct);

        // Initial full build (re-scan all sources). The startup scan already
        // captures current state, so consuming the git-push backlog adds nothing
        // but freshness going forward — hence the coalescing below. Embedding is
        // NOT done here: it is the slow, CPU-bound step and must not block the
        // consumer from starting. The EmbedLoop picks up the NULL-embedding rows
        // this scan produces, gently, in the background.
        await _core.ReindexAllAsync(ct);

        // Background loops. Fire-and-forget; the FetchAsync loop owns the foreground.
        _ = Task.Run(() => CoalesceLoopAsync(ct), ct);
        _ = Task.Run(() => _core.PeriodicRescanAsync(ct), ct);
        _ = Task.Run(() => _core.EmbedLoopAsync(ct), ct);
    }

    protected override ValueTask ProcessAsync(string subject, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        // subject: <prefix>.git.<repo>.push.<branch>. Mark the repo dirty and
        // return immediately so the event is acked fast (the backlog drains in
        // seconds); the coalescing loop does the actual re-scan, at most once
        // per debounce window regardless of how many pushes arrive.
        var repoName = RepoNameFromSubject(subject);
        if (repoName is null) return ValueTask.CompletedTask;
        if (_core.Sources.Any(r => r.Source == repoName))
            _dirty[repoName] = 1;
        else
            Log.LogDebug("git push for unwatched repo {repo} — ignored", repoName);
        return ValueTask.CompletedTask;
    }

    /// <summary>Extract the repo name from a git-push subject of the shape
    /// <c>&lt;prefix&gt;.git.&lt;repo&gt;.push.&lt;branch&gt;</c>: the token immediately
    /// after the <c>git</c> segment. Returns null when the subject does not match.</summary>
    internal static string? RepoNameFromSubject(string subject)
    {
        var parts = subject.Split('.');
        var gitIdx = Array.IndexOf(parts, "git");
        if (gitIdx < 0 || gitIdx + 1 >= parts.Length) return null;
        var repo = parts[gitIdx + 1];
        return string.IsNullOrEmpty(repo) ? null : repo;
    }

    private async Task CoalesceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_debounce, ct); }
            catch (OperationCanceledException) { break; }

            // Snapshot + clear the dirty set, then re-scan each marked repo once.
            var due = _dirty.Keys.ToArray();
            foreach (var repoName in due)
            {
                _dirty.TryRemove(repoName, out _);
                var repo = _core.Sources.FirstOrDefault(r => r.Source == repoName);
                if (repo is null) continue;
                Log.LogInformation("coalesced git push(es) on {repo} → re-scanning source", repoName);
                try { await _core.ReindexSourceAsync(repo, ct); }
                catch (Exception e) { Log.LogWarning(e, "coalesced re-index failed for {repo}", repoName); }
            }
            // Re-scans only clear embeddings on content change (UpsertAsync sets
            // embedding=NULL); the background EmbedLoop refills them. We do NOT
            // backfill inline here — that would put CPU-bound ONNX work on the
            // event-coalescing path and could saturate the host.
        }
    }

    /// <summary>Exposed for tests only — proves the resilience contract
    /// (per-doc isolation, poison-doc skip, tombstone) without a live Postgres.
    /// Delegates to the shared <see cref="IndexerCore"/> engine.</summary>
    internal Task IndexDocsAsync(string source, IReadOnlyList<Document> docs, CancellationToken ct) =>
        _core.IndexDocsAsync(source, docs, ct);
}
