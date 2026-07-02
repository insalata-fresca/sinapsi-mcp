using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using Npgsql;
using Sinapsi.Nats;

namespace Sinapsi.Indexer;

/// <summary>
/// Event-DRIVEN cache over source-of-truth repos. A durable consumer on a
/// JetStream stream, filtered to git-push notifications (env-driven subject,
/// default <c>events.git.&gt;</c>). A push to a watched repo only **marks that
/// repo dirty** (the event is acked immediately); a **coalescing loop** re-scans
/// each dirty repo at most once per debounce window (git pull → walk → idempotent
/// upsert → tombstone missing). This collapses a burst — including the one-time
/// backlog drain a fresh durable consumer sees — into a single re-scan instead of
/// one git-pull per historical event. A full re-scan also runs at startup and on a
/// periodic safety timer. The event log is never the source of truth — the repos
/// are.
/// </summary>
public sealed class IndexerWorker : JetStreamWorker
{
    private readonly IIndexStore _store;
    private readonly SourceScanner _scanner;
    private readonly IEmbedder _embedder;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _dirty = new();
    private readonly TimeSpan _rescanInterval;
    private readonly TimeSpan _debounce;

    public bool SchemaReady { get; private set; }
    public long DocsUpserted { get; private set; }
    public long DocsEmbedded { get; private set; }
    public DateTimeOffset? LastReindex { get; private set; }

    public IndexerWorker(IIndexStore store, SourceScanner scanner, IEmbedder embedder, NatsConnectionOptions opts, ILogger<IndexerWorker> log)
        : base(opts, log)
    {
        _store = store;
        _scanner = scanner;
        _embedder = embedder;
        // Fail-closed numeric config: a bad INDEXER_RESCAN_INTERVAL_MIN /
        // INDEXER_DEBOUNCE_SEC now throws (naming the var) at startup rather than
        // being silently clamped/defaulted. A valid value behaves exactly as before.
        _rescanInterval = TimeSpan.FromMinutes(IndexerConfig.RescanIntervalMin());
        _debounce = TimeSpan.FromSeconds(IndexerConfig.DebounceSec());
    }

    protected override string StreamName => Environment.GetEnvironmentVariable("INDEXER_STREAM") ?? "EVENTS";
    protected override string DurableName => Environment.GetEnvironmentVariable("INDEXER_DURABLE") ?? "sinapsi-indexer";
    // Only git-push notifications drive re-indexing — NOT every event (efficiency +
    // keeps the consumer narrow). Expected subject shape: <prefix>.git.<repo>.push.<branch>.
    protected override string FilterSubject => Environment.GetEnvironmentVariable("INDEXER_WATCH_SUBJECT") ?? "events.git.>";

    protected override async ValueTask OnStartAsync(NatsJSContext js, CancellationToken ct)
    {
        // Retry schema until Postgres is reachable (the data tier may still be booting).
        for (var attempt = 1; ; attempt++)
        {
            try { await _store.EnsureSchemaAsync(ct); SchemaReady = true; break; }
            catch (Exception e) when (attempt < 30 && !ct.IsCancellationRequested)
            {
                Log.LogWarning(e, "schema not ready (attempt {n}) — retrying in 5s", attempt);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        // Initial full build (re-scan all sources). The startup scan already
        // captures current state, so consuming the git-push backlog adds nothing
        // but freshness going forward — hence the coalescing below. Embedding is
        // NOT done here: it is the slow, CPU-bound step and must not block the
        // consumer from starting. The EmbedLoop picks up the NULL-embedding rows
        // this scan produces, gently, in the background.
        await ReindexAllAsync(ct);

        // Background loops. Fire-and-forget; the FetchAsync loop owns the foreground.
        _ = Task.Run(() => CoalesceLoopAsync(ct), ct);
        _ = Task.Run(() => PeriodicRescanAsync(ct), ct);
        _ = Task.Run(() => EmbedLoopAsync(ct), ct);
    }

    protected override ValueTask ProcessAsync(string subject, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        // subject: <prefix>.git.<repo>.push.<branch>. Mark the repo dirty and
        // return immediately so the event is acked fast (the backlog drains in
        // seconds); the coalescing loop does the actual re-scan, at most once
        // per debounce window regardless of how many pushes arrive.
        var repoName = RepoNameFromSubject(subject);
        if (repoName is null) return ValueTask.CompletedTask;
        if (_scanner.Repos.Any(r => r.Source == repoName))
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
                var repo = _scanner.Repos.FirstOrDefault(r => r.Source == repoName);
                if (repo is null) continue;
                Log.LogInformation("coalesced git push(es) on {repo} → re-scanning source", repoName);
                try { await ReindexRepoAsync(repo, ct); }
                catch (Exception e) { Log.LogWarning(e, "coalesced re-index failed for {repo}", repoName); }
            }
            // Re-scans only clear embeddings on content change (UpsertAsync sets
            // embedding=NULL); the background EmbedLoop refills them. We do NOT
            // backfill inline here — that would put CPU-bound ONNX work on the
            // event-coalescing path and could saturate the host.
        }
    }

    private async Task PeriodicRescanAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_rescanInterval, ct); }
            catch (OperationCanceledException) { break; }
            try { await ReindexAllAsync(ct); }
            catch (Exception e) { Log.LogWarning(e, "periodic rescan failed"); }
        }
    }

    private async Task ReindexAllAsync(CancellationToken ct)
    {
        foreach (var repo in _scanner.Repos)
            await ReindexRepoAsync(repo, ct);
        // No inline embedding: the background EmbedLoop owns that work so the
        // CPU-bound ONNX step never blocks scan/consume or pegs the host.
    }

    // Background embedding worker. Continuously drains NULL-embedding rows in
    // small batches with a per-doc throttle, then idles between passes. Decoupled
    // from the consumer + coalesce loops so embedding pressure can never delay
    // event acks or saturate the shared host.
    private async Task EmbedLoopAsync(CancellationToken ct)
    {
        var idle = TimeSpan.FromSeconds(IndexerConfig.EmbedIdleSec());
        while (!ct.IsCancellationRequested)
        {
            int embedded;
            try { embedded = await BackfillEmbeddingsAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Log.LogWarning(e, "embedding backfill pass failed"); embedded = 0; }
            // When a pass found nothing to do, sleep before polling again; when it
            // did work, loop straight back (more rows likely waiting) — the
            // per-doc throttle still bounds CPU.
            if (embedded == 0)
            {
                try { await Task.Delay(idle, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // Embed docs with a NULL embedding (new or content-changed → embedding was
    // cleared). Idempotent; safe to re-run. Throttles between docs to keep ONNX
    // off the host's back; returns how many it embedded this pass.
    private async Task<int> BackfillEmbeddingsAsync(CancellationToken ct)
    {
        const int batch = 32;
        var throttle = TimeSpan.FromMilliseconds(IndexerConfig.EmbedThrottleMs());
        var total = 0;
        for (var i = 0; i < 200 && !ct.IsCancellationRequested; i++)
        {
            var missing = await _store.GetMissingEmbeddingsAsync(batch, ct);
            if (missing.Count == 0) break;
            var ok = 0;
            foreach (var (id, title, body) in missing)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var text = title + "\n" + body;
                    if (text.Length > 8000) text = text[..8000];
                    await _store.SetEmbeddingAsync(id, _embedder.Embed(text), ct);
                    ok++;
                    total++;
                    DocsEmbedded++;
                }
                catch (Exception e) { Log.LogWarning(e, "embed failed for {id}", id); }
                if (throttle > TimeSpan.Zero)
                {
                    try { await Task.Delay(throttle, ct); }
                    catch (OperationCanceledException) { return total; }
                }
            }
            if (ok == 0) { Log.LogWarning("embedding backfill made no progress — stopping this pass"); break; }
        }
        return total;
    }

    private async Task ReindexRepoAsync(RepoSpec repo, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!await _scanner.SyncAsync(repo, ct))
            {
                Log.LogWarning("skipping {source} re-index — sync failed", repo.Source);
                return;
            }
            var docs = _scanner.Scan(repo);
            await IndexDocsAsync(repo.Source, docs, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Upsert every scanned doc of a source with PER-DOC isolation, then tombstone
    /// the docs that disappeared. A single poison document (content Postgres rejects
    /// — a stray NUL byte the scanner missed → SqlState 22021, or any other
    /// data-dependent server-side error) must NOT abort the rest of the source's
    /// scan nor, via an escape out of the background loop, kill the whole service:
    /// it is counted, warned, and skipped. Genuinely fatal problems (connection down,
    /// auth/schema failure) surface as <see cref="NpgsqlException"/> — NOT
    /// <see cref="PostgresException"/> — so they still propagate to the outer
    /// retry/backoff. Internal (not private) so the resilience is unit-testable with a
    /// mock store, no git and no live Postgres.
    /// </summary>
    internal async Task IndexDocsAsync(string source, IReadOnlyList<Document> docs, CancellationToken ct)
    {
        var changed = 0;
        var failed = 0;
        var present = new List<string>(docs.Count);
        foreach (var doc in docs)
        {
            // Record the DocId as "present" even on a failed upsert so a transient
            // per-doc failure does not make the tombstone pass wrongly delete an
            // already-indexed row.
            present.Add(doc.DocId);
            try
            {
                if (await _store.UpsertAsync(doc, ct)) changed++;
            }
            catch (PostgresException e)
            {
                failed++;
                Log.LogWarning(e, "skipping doc {docId} in {source}: upsert rejected (SqlState {state})",
                    doc.DocId, source, e.SqlState);
            }
        }
        var tombstoned = await _store.TombstoneMissingAsync(source, present, ct);
        DocsUpserted += changed;
        LastReindex = DateTimeOffset.UtcNow;
        if (failed > 0)
            Log.LogWarning("re-indexed {source}: {total} docs, {changed} changed, {failed} failed, {tomb} tombstoned",
                source, docs.Count, changed, failed, tombstoned);
        else
            Log.LogInformation("re-indexed {source}: {total} docs, {changed} changed, {tomb} tombstoned",
                source, docs.Count, changed, tombstoned);
    }
}
