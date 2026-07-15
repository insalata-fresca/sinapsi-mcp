// ---------------------------------------------------------------------------
// IndexerCore - the reindex/embed engine shared by BOTH index-worker shapes:
//   - IndexerWorker (shared-bus): NATS-driven coalesce + this core's periodic
//     rescan + embed loop.
//   - TimerOnlyIndexWorker (isolated, INDEXER_NATS_MODE=isolated): this core's
//     periodic rescan + embed loop ONLY — no NATS consumer, no coalesce (there
//     is no push signal to coalesce), no NATS client of any kind.
// Extracted from IndexerWorker (indexer-generalization) so the reindex/embed
// logic has exactly one implementation regardless of which shell owns it.
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using Npgsql;

namespace Sinapsi.Indexer;

/// <summary>
/// The engine behind "index": re-scan sources (git pull -> walk -> classify ->
/// hash), idempotent per-doc upsert with poison-doc isolation, tombstone of
/// vanished files, and a decoupled background embedding backfill. Holds no NATS
/// state — it is reachable identically whether the caller is NATS-driven
/// (shared-bus) or timer-only (isolated).
/// </summary>
internal sealed class IndexerCore
{
    private readonly IIndexStore _store;
    private readonly ISourceScanner _scanner;
    private readonly IEmbedder _embedder;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _rescanInterval;

    public bool SchemaReady { get; private set; }
    public long DocsUpserted { get; private set; }
    public long DocsEmbedded { get; private set; }
    public DateTimeOffset? LastReindex { get; private set; }

    public IndexerCore(IIndexStore store, ISourceScanner scanner, IEmbedder embedder, ILogger log)
    {
        _store = store;
        _scanner = scanner;
        _embedder = embedder;
        _log = log;
        // Fail-closed numeric config: a bad INDEXER_RESCAN_INTERVAL_MIN now
        // throws (naming the var) at startup rather than being silently
        // clamped/defaulted. A valid value behaves exactly as before.
        _rescanInterval = TimeSpan.FromMinutes(IndexerConfig.RescanIntervalMin());
    }

    /// <summary>The tracked sources as source-neutral handles (the workers match
    /// a push subject's repo token against each handle's <see cref="ISourceRef.Source"/>).</summary>
    public IReadOnlyList<ISourceRef> Sources => _scanner.Sources;

    /// <summary>Retry schema-ensure until Postgres is reachable (the data tier
    /// may still be booting). Sets <see cref="SchemaReady"/> once it succeeds.</summary>
    public async Task EnsureSchemaWithRetryAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await _store.EnsureSchemaAsync(ct); SchemaReady = true; break; }
            catch (Exception e) when (attempt < 30 && !ct.IsCancellationRequested)
            {
                _log.LogWarning(e, "schema not ready (attempt {n}) — retrying in 5s", attempt);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    public async Task ReindexAllAsync(CancellationToken ct)
    {
        foreach (var source in _scanner.Sources)
            await ReindexSourceAsync(source, ct);
        // No inline embedding: the background EmbedLoop owns that work so the
        // CPU-bound ONNX step never blocks scan/consume or pegs the host.

        await PruneRetiredSourcesAsync(ct);
    }

    /// <summary>
    /// Auto-tombstone docs whose <c>source</c> is no longer in the tenant's
    /// CONFIGURED source set (e.g. a tenant repointed from a git source to an
    /// OPDS source — the old source is never scanned again so its docs would
    /// otherwise linger forever and pollute search). Runs once per
    /// <see cref="ReindexAllAsync"/> pass, after every configured source has had
    /// its scan attempted.
    /// <para>
    /// Precision: <c>keepSources</c> is the set of ALL CONFIGURED sources
    /// (<c>_scanner.Sources</c>), regardless of whether each one's
    /// <see cref="ReindexSourceAsync"/> succeeded THIS pass. A source that merely
    /// had a transient <c>SyncAsync</c> failure this pass is still in the config
    /// -> still kept -> its docs are NEVER tombstoned by this path (only
    /// <see cref="IIndexStore.TombstoneMissingAsync"/>, scoped to a source that
    /// itself scanned successfully, ever removes docs within a live source).
    /// Only a source that was REMOVED from config entirely gets pruned here.
    /// </para>
    /// <para>
    /// HARD fail-safe: if the configured-source set is empty (config misparse /
    /// no sources wired), this is a no-op — never wipe the whole store because
    /// of a bad/empty config.
    /// </para></summary>
    private async Task PruneRetiredSourcesAsync(CancellationToken ct)
    {
        var keepSources = _scanner.Sources.Select(s => s.Source).Distinct().ToArray();
        if (keepSources.Length == 0)
        {
            _log.LogWarning("skipping retired-source prune — configured source set is empty (refusing to tombstone everything)");
            return;
        }

        var pruned = await _store.TombstoneSourcesNotInAsync(keepSources, ct);
        if (pruned > 0)
            _log.LogInformation("pruned {count} docs from sources no longer configured (kept: {kept})",
                pruned, string.Join(", ", keepSources));
    }

    public async Task ReindexSourceAsync(ISourceRef source, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!await _scanner.SyncAsync(source, ct))
            {
                _log.LogWarning("skipping {source} re-index — sync failed", source.Source);
                return;
            }
            var docs = _scanner.Scan(source);
            await IndexDocsAsync(source.Source, docs, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Run the periodic safety re-scan on <c>INDEXER_RESCAN_INTERVAL_MIN</c>.
    /// Runs forever until cancelled — the caller decides whether this is the ONLY
    /// freshness path (isolated mode) or a safety-net alongside push-coalescing
    /// (shared-bus mode).</summary>
    public async Task PeriodicRescanAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_rescanInterval, ct); }
            catch (OperationCanceledException) { break; }
            try { await ReindexAllAsync(ct); }
            catch (Exception e) { _log.LogWarning(e, "periodic rescan failed"); }
        }
    }

    // Background embedding worker. Continuously drains NULL-embedding rows in
    // small batches with a per-doc throttle, then idles between passes. Decoupled
    // from the consumer/coalesce/timer loops so embedding pressure can never
    // delay event acks or saturate the shared host.
    public async Task EmbedLoopAsync(CancellationToken ct)
    {
        var idle = TimeSpan.FromSeconds(IndexerConfig.EmbedIdleSec());
        while (!ct.IsCancellationRequested)
        {
            int embedded;
            try { embedded = await BackfillEmbeddingsAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { _log.LogWarning(e, "embedding backfill pass failed"); embedded = 0; }
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
                catch (Exception e) { _log.LogWarning(e, "embed failed for {id}", id); }
                if (throttle > TimeSpan.Zero)
                {
                    try { await Task.Delay(throttle, ct); }
                    catch (OperationCanceledException) { return total; }
                }
            }
            if (ok == 0) { _log.LogWarning("embedding backfill made no progress — stopping this pass"); break; }
        }
        return total;
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
                _log.LogWarning(e, "skipping doc {docId} in {source}: upsert rejected (SqlState {state})",
                    doc.DocId, source, e.SqlState);
            }
        }
        var tombstoned = await _store.TombstoneMissingAsync(source, present, ct);
        DocsUpserted += changed;
        LastReindex = DateTimeOffset.UtcNow;
        if (failed > 0)
            _log.LogWarning("re-indexed {source}: {total} docs, {changed} changed, {failed} failed, {tomb} tombstoned",
                source, docs.Count, changed, failed, tombstoned);
        else
            _log.LogInformation("re-indexed {source}: {total} docs, {changed} changed, {tomb} tombstoned",
                source, docs.Count, changed, tombstoned);
    }
}
