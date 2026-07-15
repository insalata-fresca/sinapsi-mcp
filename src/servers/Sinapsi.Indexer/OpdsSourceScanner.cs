// ---------------------------------------------------------------------------
// OpdsSourceScanner - the OPDS implementation of the ISourceScanner seam (M4).
//
// Where GitSourceScanner is "git clone/fetch a repo -> walk *.md -> Document",
// this is "poll an OPDS acquisition feed -> download each EPUB -> extract ->
// chunk -> Document". It composes the M2 (OpdsClient + EpubExtractor) and M3
// (BookChunker + BookDocumentMapper) building blocks behind the SAME three-op
// seam (Sources / SyncAsync / Scan) that IndexerCore + both worker shells
// already drive, so the core is untouched and the git path is unaffected.
//
// Sync/Scan split (mirrors the git scanner):
//   * SyncAsync(source): does ALL the network + CPU work — enumerate the feed,
//     diff against the prior snapshot, download+extract+chunk the new/changed
//     entries (reusing cached Documents for unchanged ones), and HOLD the full
//     present-entry Document set in memory. Returns false on a whole-feed
//     failure so the core skips scanning (never indexes a stale/empty snapshot).
//   * Scan(source): returns the Documents SyncAsync produced — synchronous, no
//     I/O, exactly as the seam requires. The full present set is returned so
//     IndexerCore.IndexDocsAsync -> TombstoneMissingAsync soft-deletes the
//     chunks of entries that vanished from the feed (tombstone-on-remove).
//
// Poison-doc isolation: a single bad entry (download error, corrupt EPUB,
// extraction/chunking failure) is caught, logged, and SKIPPED — it never aborts
// the batch. Its prior Documents are dropped from the present set so, if it is
// genuinely gone, its chunks tombstone; if it is a transient fetch error the
// next rescan re-adds it. (Per-doc Postgres-reject isolation is already handled
// downstream in IndexerCore.IndexDocsAsync.)
//
// Pacing: SyncAsync runs on the existing rescan cadence (the core's periodic
// timer / a git-push has no analogue here) and throttles between downloads by
// INDEXER_OPDS_DOWNLOAD_THROTTLE_MS so a large library does not spike disk/CPU
// on the shared CT132-observability host. Embedding is still governed by the
// existing CPUQuota + embed-throttle in IndexerCore.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using Sinapsi.Opds;
using Sinapsi.Opds.Models;

namespace Sinapsi.Indexer;

/// <summary>An OPDS catalogue source: a logical name + the acquisition feed URL.
/// The <see cref="Source"/> logical name is the only field that crosses the
/// <see cref="ISourceScanner"/> seam (via <see cref="ISourceRef"/>); the feed URL
/// is OPDS-specific detail private to <see cref="OpdsSourceScanner"/>.</summary>
public sealed record OpdsSourceRef(string Source, string FeedUrl) : ISourceRef;

/// <summary>
/// OPDS implementation of the <see cref="ISourceScanner"/> seam: polls an OPDS
/// acquisition feed, downloads + extracts + chunks each EPUB into
/// <see cref="Document"/>s, and holds the present-entry Document set for the
/// synchronous <see cref="Scan"/> to return. One configured OPDS source for now
/// (the books profile); the shape supports more without touching the core.
/// </summary>
public sealed class OpdsSourceScanner : ISourceScanner
{
    private readonly IReadOnlyList<OpdsSourceRef> _sources;
    private readonly OpdsClient _client;
    private readonly TimeSpan _downloadThrottle;
    private readonly ILogger _log;

    // Per-source state carried between SyncAsync (produces) and Scan (consumes),
    // and across rescans (the diff optimization). Keyed by logical source name.
    //   _snapshots: {entry Id -> change-token} from the LAST successful enumerate,
    //               handed to OpdsClient.Diff to skip re-downloading unchanged entries.
    //   _docsByEntry: {entry Id -> its produced Documents} so an unchanged entry's
    //               chunks are reused verbatim (no re-download) and a removed
    //               entry's chunks drop out of the present set (-> tombstoned).
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, IReadOnlyList<Document>>> _docsByEntry = new(StringComparer.Ordinal);

    // The Documents SyncAsync produced for each source, flattened, ready for the
    // synchronous Scan to hand back. Replaced wholesale on each successful sync.
    private readonly Dictionary<string, IReadOnlyList<Document>> _scanned = new(StringComparer.Ordinal);

    private readonly object _lock = new();

    public OpdsSourceScanner(
        IReadOnlyList<OpdsSourceRef> sources,
        OpdsClient client,
        int downloadThrottleMs,
        ILogger log)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _downloadThrottle = TimeSpan.FromMilliseconds(Math.Max(0, downloadThrottleMs));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    // --- ISourceScanner seam -------------------------------------------------

    IReadOnlyList<ISourceRef> ISourceScanner.Sources => _sources;

    /// <summary>The OPDS sources this scanner tracks (concrete refs).</summary>
    public IReadOnlyList<OpdsSourceRef> Sources => _sources;

    /// <summary>
    /// Refresh ONE OPDS source: enumerate the feed, diff vs the prior snapshot,
    /// download+extract+chunk the new/changed entries (reuse cached Documents for
    /// unchanged ones), and hold the full present-entry Document set for
    /// <see cref="Scan"/>. Returns false on a WHOLE-FEED failure (enumerate threw)
    /// so the core skips scanning rather than tombstoning a real catalog against
    /// an empty snapshot. A per-ENTRY failure is isolated (logged + skipped),
    /// never aborting the batch. Must not throw for an ordinary sync failure.
    /// </summary>
    public async Task<bool> SyncAsync(ISourceRef source, CancellationToken ct)
    {
        var opds = AsOpds(source);

        IReadOnlyList<OpdsEntry> current;
        try
        {
            current = await _client.EnumerateAllAsync(opds.FeedUrl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // cancellation is not a sync failure — let it propagate
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "OPDS enumerate failed for {source} ({url}) — skipping this pass",
                opds.Source, opds.FeedUrl);
            return false;
        }

        // Snapshot + prior per-entry docs for this source (empty on first run).
        IReadOnlyDictionary<string, string> prevSnapshot;
        Dictionary<string, IReadOnlyList<Document>> priorDocs;
        lock (_lock)
        {
            prevSnapshot = _snapshots.TryGetValue(opds.Source, out var s) ? s
                : new Dictionary<string, string>(StringComparer.Ordinal);
            priorDocs = _docsByEntry.TryGetValue(opds.Source, out var d)
                ? new Dictionary<string, IReadOnlyList<Document>>(d, StringComparer.Ordinal)
                : new Dictionary<string, IReadOnlyList<Document>>(StringComparer.Ordinal);
        }

        var diff = OpdsClient.Diff(prevSnapshot, current);
        // Entries whose bytes we must (re)fetch: new + changed. Unchanged entries
        // reuse their cached Documents (the diff is a download OPTIMIZATION — the
        // present set below is rebuilt from ALL current entries regardless).
        var mustFetch = new HashSet<string>(
            diff.New.Concat(diff.Changed).Select(e => e.Id), StringComparer.Ordinal);

        var nextDocs = new Dictionary<string, IReadOnlyList<Document>>(StringComparer.Ordinal);
        var produced = new List<Document>();
        int fetched = 0, reused = 0, skipped = 0;

        foreach (var entry in current)
        {
            if (ct.IsCancellationRequested) break;

            // Reuse cached Documents for an unchanged entry we already have — no
            // re-download. Correctness never depends on this: on a cache miss the
            // entry falls through to a full (re)fetch below.
            if (!mustFetch.Contains(entry.Id)
                && priorDocs.TryGetValue(entry.Id, out var cached))
            {
                nextDocs[entry.Id] = cached;
                produced.AddRange(cached);
                reused++;
                continue;
            }

            // New / changed (or cache-missed unchanged) — download + extract +
            // chunk. Poison isolation: ANY failure for THIS entry is logged and
            // skipped; the batch continues. A skipped entry contributes no
            // Documents this pass, so if it is genuinely gone its chunks tombstone;
            // a transient error simply re-adds it on the next rescan.
            try
            {
                var docs = await ProduceDocumentsAsync(opds.Source, entry, ct).ConfigureAwait(false);
                nextDocs[entry.Id] = docs;
                produced.AddRange(docs);
                fetched++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                skipped++;
                _log.LogWarning(e, "skipping OPDS entry {id} ('{title}') in {source}: {reason}",
                    entry.Id, entry.Title, opds.Source, e.Message);
            }

            // Pace downloads so a big library doesn't spike disk/CPU on the shared host.
            if (_downloadThrottle > TimeSpan.Zero && !ct.IsCancellationRequested)
            {
                try { await Task.Delay(_downloadThrottle, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        // Commit the new present set + snapshot atomically for Scan to read.
        // The snapshot is rebuilt from the CURRENT entries so the next diff sees
        // exactly what we enumerated (not what we successfully produced) — an
        // entry that failed to download this pass is still "known" and will be
        // retried (as changed/new) next time rather than re-flagged forever.
        lock (_lock)
        {
            _scanned[opds.Source] = produced;
            _docsByEntry[opds.Source] = nextDocs;
            _snapshots[opds.Source] = OpdsClient.Snapshot(current);
        }

        _log.LogInformation(
            "OPDS sync {source}: {entries} entries ({fetched} fetched, {reused} unchanged-reused, {skipped} skipped) -> {docs} chunk-documents",
            opds.Source, current.Count, fetched, reused, skipped, produced.Count);
        return true;
    }

    /// <summary>Return the Documents <see cref="SyncAsync"/> produced for this
    /// source (the full present set — new/changed freshly chunked, unchanged
    /// reused). Synchronous + side-effect-free, as the seam requires; the present
    /// set drives the core's tombstone-of-vanished-entries pass.</summary>
    public IReadOnlyList<Document> Scan(ISourceRef source)
    {
        var opds = AsOpds(source);
        lock (_lock)
        {
            return _scanned.TryGetValue(opds.Source, out var docs) ? docs : Array.Empty<Document>();
        }
    }

    // --- production ----------------------------------------------------------

    /// <summary>Download ONE entry's EPUB, extract it, chunk it, and map each
    /// chunk to a <see cref="Document"/>. An entry with no EPUB acquisition link
    /// is skipped cleanly (empty result, not an error). Throws on a download /
    /// extraction failure so the caller's per-entry catch isolates it.</summary>
    private async Task<IReadOnlyList<Document>> ProduceDocumentsAsync(
        string source, OpdsEntry entry, CancellationToken ct)
    {
        if (entry.EpubLink is not { } link)
        {
            _log.LogDebug("OPDS entry {id} ('{title}') has no EPUB acquisition link — skipped",
                entry.Id, entry.Title);
            return Array.Empty<Document>();
        }

        var bytes = await _client.DownloadAsync(link, ct).ConfigureAwait(false);
        var book = EpubExtractor.Extract(bytes);

        // Carry the OPDS entry's canonical facets onto every chunk (the entry's
        // title/authors/categories/identifier are usually richer than the OPF).
        var chunks = BookChunker.Chunk(
            book,
            fallbackId: entry.Id,
            isbn: entry.Identifier,
            title: entry.Title,
            authors: entry.Authors,
            categories: entry.Categories);

        var docs = new List<Document>(chunks.Count);
        foreach (var chunk in chunks)
            docs.Add(BookDocumentMapper.ToDocument(chunk, source));
        return docs;
    }

    /// <summary>Every handle an OpdsSourceScanner produces is an
    /// <see cref="OpdsSourceRef"/>; a foreign <see cref="ISourceRef"/> reaching
    /// this scanner is a composition bug, surfaced loudly.</summary>
    private static OpdsSourceRef AsOpds(ISourceRef source) => source as OpdsSourceRef
        ?? throw new ArgumentException(
            $"OpdsSourceScanner requires an OpdsSourceRef source ref, got {source.GetType().Name}", nameof(source));

    // --- composition from env ------------------------------------------------

    /// <summary>
    /// Build the single configured OPDS source from env. <c>INDEXER_OPDS_URL</c>
    /// is REQUIRED (fail-closed — the composition root refuses to start without
    /// it when source-kind is opds); <c>INDEXER_OPDS_SOURCE</c> is the logical
    /// name (default "books"). Never bakes in a URL.
    /// </summary>
    public static IReadOnlyList<OpdsSourceRef> SourcesFromEnv()
    {
        var url = Env("INDEXER_OPDS_URL");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                "INDEXER_OPDS_URL is required when INDEXER_SOURCE_KIND=opds (the OPDS acquisition feed URL).");
        var name = Env("INDEXER_OPDS_SOURCE") is { Length: > 0 } n ? n : "books";
        return new[] { new OpdsSourceRef(name, url.Trim()) };
    }

    /// <summary>Build the <see cref="OpdsClientOptions"/> (Basic auth + limits)
    /// from env. Anonymous when <c>INDEXER_OPDS_USER</c> is unset.</summary>
    public static OpdsClientOptions ClientOptionsFromEnv() => new()
    {
        Username = Env("INDEXER_OPDS_USER"),
        Password = Env("INDEXER_OPDS_PASSWORD"),
    };

    private static string? Env(string k) =>
        Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : null;
}
