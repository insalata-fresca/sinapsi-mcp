// ---------------------------------------------------------------------------
// OpdsSourceScannerTests - the OpdsSourceScanner behind the ISourceScanner seam,
// exercised end-to-end (synthetic OPDS feed -> synthetic EPUB -> BookChunker ->
// BookDocumentMapper -> Document) over a canned HttpMessageHandler. NO network,
// NO Postgres, NO ONNX.
//
// Proves the M4 Card's acceptance points:
//   * new entry           -> Documents produced (key + jsonb metadata correct)
//   * changed entry        -> re-downloaded + re-upserted
//   * unchanged entry     -> NOT re-downloaded (diff optimization), still present
//   * removed entry        -> absent from Scan -> tombstoned via the core flow
//   * poison entry         -> isolated (logged + skipped), the batch still completes
//   * whole-feed failure   -> SyncAsync returns false (core skips scanning)
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sinapsi.Opds;
using Xunit;
using static Sinapsi.Indexer.Tests.OpdsTestFixtures;

namespace Sinapsi.Indexer.Tests;

public sealed class OpdsSourceScannerTests
{
    private const string Source = "books";
    private static readonly OpdsSourceRef Ref = new(Source, FeedUrl);

    private static OpdsSourceScanner NewScanner(CannedOpdsHandler handler) =>
        new(
            new[] { Ref },
            new OpdsClient(new HttpClient(handler)),
            downloadThrottleMs: 0, // no delay in tests
            NullLogger.Instance);

    // ── new entry -> Documents with correct key + metadata ──────────────────

    [Fact]
    public async Task Sync_new_entry_produces_documents_with_correct_key_and_metadata()
    {
        var handler = new CannedOpdsHandler(() =>
            AcqFeed(("urn:isbn:111", "Clean Code", "2026-01-01T00:00:00Z", "a")))
            .Epub("a", BuildEpub("Clean Code", "urn:isbn:111", "Refactoring", LongBody("alpha")));
        var scanner = NewScanner(handler);

        Assert.True(await scanner.SyncAsync(Ref, CancellationToken.None));
        var docs = scanner.Scan(Ref);

        Assert.NotEmpty(docs);
        var doc = docs[0];
        Assert.Equal(Source, doc.Source);
        Assert.Equal(BookDocumentMapper.BookChunkKind, doc.Kind);
        // DocId == BookChunker key "book:<isbn>:<chapterOrder>:<chunkIndex>".
        Assert.StartsWith("book:urn:isbn:111:0:", doc.DocId);
        Assert.Equal(doc.DocId, docs[0].DocId);
        // Facet metadata is a real jsonb object carrying the OPDS facets.
        Assert.NotNull(doc.Metadata);
        using var meta = JsonDocument.Parse(doc.Metadata!);
        Assert.Equal("urn:isbn:111", meta.RootElement.GetProperty("isbn").GetString());
        Assert.Equal("Software Engineering", meta.RootElement.GetProperty("categories")[0].GetString());
        Assert.Contains("Clean Code", doc.Title);
    }

    // ── unchanged entry -> not re-downloaded, still present ──────────────────

    [Fact]
    public async Task Sync_unchanged_entry_is_not_redownloaded_but_stays_present()
    {
        var feed = AcqFeed(("urn:isbn:111", "Clean Code", "2026-01-01T00:00:00Z", "a"));
        var handler = new CannedOpdsHandler(() => feed)
            .Epub("a", BuildEpub("Clean Code", "urn:isbn:111", "Refactoring", LongBody("alpha")));
        var scanner = NewScanner(handler);

        await scanner.SyncAsync(Ref, CancellationToken.None);
        var firstDocs = scanner.Scan(Ref);
        Assert.Single(handler.Downloaded, "a");

        // Second sync, identical feed (same <updated> change-token) => no re-download.
        await scanner.SyncAsync(Ref, CancellationToken.None);
        var secondDocs = scanner.Scan(Ref);

        Assert.Equal(new[] { "a" }, handler.Downloaded); // still exactly one download
        // Present set unchanged (same DocIds reused from cache).
        Assert.Equal(
            firstDocs.Select(d => d.DocId).OrderBy(x => x),
            secondDocs.Select(d => d.DocId).OrderBy(x => x));
    }

    // ── changed entry -> re-downloaded + re-upserted ─────────────────────────

    [Fact]
    public async Task Sync_changed_entry_is_redownloaded()
    {
        var updated = "2026-01-01T00:00:00Z";
        // The feed is rebuilt each fetch so we can advance the change-token.
        var handler = new CannedOpdsHandler(() =>
            AcqFeed(("urn:isbn:111", "Clean Code", updated, "a")))
            .Epub("a", BuildEpub("Clean Code", "urn:isbn:111", "Refactoring", LongBody("alpha")));
        var scanner = NewScanner(handler);

        await scanner.SyncAsync(Ref, CancellationToken.None);
        Assert.Equal(new[] { "a" }, handler.Downloaded);

        // Advance the change-token -> the entry counts as "changed".
        updated = "2026-06-01T00:00:00Z";
        await scanner.SyncAsync(Ref, CancellationToken.None);

        Assert.Equal(new[] { "a", "a" }, handler.Downloaded); // re-downloaded
    }

    // ── removed entry -> absent from Scan -> tombstoned via the core ─────────

    [Fact]
    public async Task Removed_entry_drops_from_scan_and_is_tombstoned_by_the_core()
    {
        var both = AcqFeed(
            ("urn:isbn:111", "Book A", "2026-01-01T00:00:00Z", "a"),
            ("urn:isbn:222", "Book B", "2026-01-01T00:00:00Z", "b"));
        var onlyA = AcqFeed(("urn:isbn:111", "Book A", "2026-01-01T00:00:00Z", "a"));
        var feed = both;
        var handler = new CannedOpdsHandler(() => feed)
            .Epub("a", BuildEpub("Book A", "urn:isbn:111", "Chapter A", LongBody("alpha")))
            .Epub("b", BuildEpub("Book B", "urn:isbn:222", "Chapter B", LongBody("beta")));
        var scanner = NewScanner(handler);
        var store = new PresentRecordingStore();
        var core = new IndexerCore(store, scanner, new FakeEmbedder(), NullLogger.Instance);

        // Pass 1: both books present.
        await core.ReindexSourceAsync(Ref, CancellationToken.None);
        Assert.Contains(store.LastPresent!, id => id.StartsWith("book:urn:isbn:222:", StringComparison.Ordinal));

        // Pass 2: book B removed from the feed.
        feed = onlyA;
        await core.ReindexSourceAsync(Ref, CancellationToken.None);

        // Scan no longer returns B's chunks, so the tombstone present-set excludes
        // them (TombstoneMissingAsync soft-deletes what's absent).
        Assert.DoesNotContain(store.LastPresent!, id => id.StartsWith("book:urn:isbn:222:", StringComparison.Ordinal));
        Assert.Contains(store.LastPresent!, id => id.StartsWith("book:urn:isbn:111:", StringComparison.Ordinal));
        Assert.Equal(Source, store.LastTombstoneSource);
    }

    // ── poison entry -> isolated (logged + skipped), batch still completes ───

    [Fact]
    public async Task Poison_entry_is_isolated_and_the_rest_are_indexed()
    {
        var handler = new CannedOpdsHandler(() => AcqFeed(
                ("urn:isbn:111", "Good A", "2026-01-01T00:00:00Z", "a"),
                ("urn:isbn:222", "Bad B", "2026-01-01T00:00:00Z", "b"),
                ("urn:isbn:333", "Good C", "2026-01-01T00:00:00Z", "c")))
            .Epub("a", BuildEpub("Good A", "urn:isbn:111", "Chapter A", LongBody("alpha")))
            .Poison("b") // download 404s -> the entry throws -> must be skipped
            .Epub("c", BuildEpub("Good C", "urn:isbn:333", "Chapter C", LongBody("gamma")));
        var scanner = NewScanner(handler);

        // The whole-feed sync still SUCCEEDS despite the one bad entry.
        Assert.True(await scanner.SyncAsync(Ref, CancellationToken.None));
        var docs = scanner.Scan(Ref);

        // A + C produced chunks; B produced none (it was skipped).
        Assert.Contains(docs, d => d.DocId.StartsWith("book:urn:isbn:111:", StringComparison.Ordinal));
        Assert.Contains(docs, d => d.DocId.StartsWith("book:urn:isbn:333:", StringComparison.Ordinal));
        Assert.DoesNotContain(docs, d => d.DocId.StartsWith("book:urn:isbn:222:", StringComparison.Ordinal));
    }

    // ── whole-feed failure -> SyncAsync returns false (core skips) ───────────

    [Fact]
    public async Task Whole_feed_enumerate_failure_returns_false()
    {
        // Handler that 500s the feed request itself.
        var handler = new AlwaysErrorHandler();
        var scanner = new OpdsSourceScanner(
            new[] { Ref }, new OpdsClient(new HttpClient(handler)), 0, NullLogger.Instance);

        Assert.False(await scanner.SyncAsync(Ref, CancellationToken.None));
        Assert.Empty(scanner.Scan(Ref));
    }

    private sealed class AlwaysErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    // ── composition-root fail-closed ─────────────────────────────────────────

    [Fact]
    public void SourcesFromEnv_throws_when_url_missing()
    {
        var prev = Environment.GetEnvironmentVariable("INDEXER_OPDS_URL");
        try
        {
            Environment.SetEnvironmentVariable("INDEXER_OPDS_URL", null);
            Assert.Throws<InvalidOperationException>(() => OpdsSourceScanner.SourcesFromEnv());
        }
        finally { Environment.SetEnvironmentVariable("INDEXER_OPDS_URL", prev); }
    }

    [Fact]
    public void SourcesFromEnv_uses_url_and_default_source_name()
    {
        var prevUrl = Environment.GetEnvironmentVariable("INDEXER_OPDS_URL");
        var prevSrc = Environment.GetEnvironmentVariable("INDEXER_OPDS_SOURCE");
        try
        {
            Environment.SetEnvironmentVariable("INDEXER_OPDS_URL", "http://feed.test/opds");
            Environment.SetEnvironmentVariable("INDEXER_OPDS_SOURCE", null);
            var sources = OpdsSourceScanner.SourcesFromEnv();
            var one = Assert.Single(sources);
            Assert.Equal("books", one.Source);
            Assert.Equal("http://feed.test/opds", one.FeedUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INDEXER_OPDS_URL", prevUrl);
            Environment.SetEnvironmentVariable("INDEXER_OPDS_SOURCE", prevSrc);
        }
    }

    /// <summary>A minimal store that records the present-set handed to the
    /// tombstone pass (all we need to prove tombstone-on-remove); every read
    /// method is unused here.</summary>
    private sealed class PresentRecordingStore : IIndexStore
    {
        public string[]? LastPresent { get; private set; }
        public string? LastTombstoneSource { get; private set; }

        public Task<bool> UpsertAsync(Document doc, CancellationToken ct) => Task.FromResult(true);

        public Task<int> TombstoneMissingAsync(string source, IReadOnlyCollection<string> presentDocIds, CancellationToken ct)
        {
            LastTombstoneSource = source;
            LastPresent = presentDocIds.ToArray();
            return Task.FromResult(0);
        }

        public Task<int> TombstoneSourcesNotInAsync(IReadOnlyCollection<string> keepSources, CancellationToken ct) => Task.FromResult(0);

        public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;
        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SearchHit>> SearchAsync(string q, string? source, string? kind, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<LearningHit>> GetLearningsAsync(string? scope, string? query, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task SetEmbeddingAsync(string docId, float[] vector, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<(string DocId, string Title, string Body)>> GetMissingEmbeddingsAsync(int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(string, string, string)>>(Array.Empty<(string, string, string)>());
        public Task<IReadOnlyList<SearchHit>> SemanticSearchAsync(float[] queryVector, string queryText, int limit, CancellationToken ct) => throw new NotSupportedException();
    }
}
