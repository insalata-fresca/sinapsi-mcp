// ---------------------------------------------------------------------------
// RetiredSourcePruneTests - Mission A1: auto-tombstone docs from sources NO
// LONGER in the tenant's configured source set (e.g. a tenant repointed from
// a git source to an OPDS source — the old source is never scanned again so
// its docs would otherwise linger forever, is_deleted=false, and pollute
// semantic_search).
//
// Two layers:
//   1. Hermetic (no Postgres): IndexerCore.ReindexAllAsync -> PruneRetiredSourcesAsync
//      precision, proven against a fake IIndexStore + fake ISourceScanner.
//        - the empty-configured-source-set fail-safe (never tombstone everything)
//        - keepSources == ALL configured sources (scanner.Sources), regardless
//          of per-source SyncAsync success/failure this pass
//   2. DB-gated (INDEXER_DB_HOST set): PostgresIndexStore.TombstoneSourcesNotInAsync
//      against a real Postgres — retired source's docs get tombstoned, a kept
//      source's docs are untouched, and the empty-keepSources guard is a no-op
//      even at the store layer (defence in depth).
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Sinapsi.Indexer.Tests;

// ── hermetic fakes ──────────────────────────────────────────────────────────

/// <summary>A minimal source-neutral handle for the fake scanner below.</summary>
internal sealed record FakeSourceRef(string Source) : ISourceRef;

/// <summary>
/// A fully-controllable <see cref="ISourceScanner"/>: the configured source list
/// is fixed at construction (mirrors "the tenant's configured source set"), and
/// <see cref="SyncAsync"/> outcome per source is caller-supplied — letting a test
/// simulate "this source is configured but its sync failed THIS pass" without
/// removing it from the configured set.
/// </summary>
internal sealed class FakeSourceScanner : ISourceScanner
{
    private readonly IReadOnlyList<ISourceRef> _sources;
    private readonly HashSet<string> _failSync;

    public FakeSourceScanner(IReadOnlyList<string> configuredSources, IReadOnlyList<string>? failSyncFor = null)
    {
        _sources = configuredSources.Select(s => (ISourceRef)new FakeSourceRef(s)).ToArray();
        _failSync = new HashSet<string>(failSyncFor ?? Array.Empty<string>());
    }

    public IReadOnlyList<ISourceRef> Sources => _sources;

    public Task<bool> SyncAsync(ISourceRef source, CancellationToken ct) =>
        Task.FromResult(!_failSync.Contains(source.Source));

    public IReadOnlyList<Document> Scan(ISourceRef source) => Array.Empty<Document>();
}

/// <summary>
/// An <see cref="IIndexStore"/> that records every call to
/// <see cref="TombstoneSourcesNotInAsync"/> (count of invocations + the
/// keepSources handed each time) so a test can assert both "did it run" and
/// "with exactly which sources".
/// </summary>
internal sealed class PruneRecordingIndexStore : IIndexStore
{
    public List<string[]> PruneCalls { get; } = new();
    public int PruneReturns { get; set; }

    public Task<int> TombstoneSourcesNotInAsync(IReadOnlyCollection<string> keepSources, CancellationToken ct)
    {
        PruneCalls.Add(keepSources.ToArray());
        return Task.FromResult(PruneReturns);
    }

    public Task<bool> UpsertAsync(Document doc, CancellationToken ct) => Task.FromResult(true);
    public Task<int> TombstoneMissingAsync(string source, IReadOnlyCollection<string> presentDocIds, CancellationToken ct) => Task.FromResult(0);
    public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;
    public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<SearchHit>> SearchAsync(string q, string? source, string? kind, int limit, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<LearningHit>> GetLearningsAsync(string? scope, string? query, int limit, CancellationToken ct) => throw new NotSupportedException();
    public Task SetEmbeddingAsync(string docId, float[] vector, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<(string DocId, string Title, string Body)>> GetMissingEmbeddingsAsync(int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<(string, string, string)>>(Array.Empty<(string, string, string)>());
    public Task<IReadOnlyList<SearchHit>> SemanticSearchAsync(float[] queryVector, string queryText, int limit, CancellationToken ct) => throw new NotSupportedException();
}

// ── hermetic: IndexerCore.ReindexAllAsync -> prune precision ───────────────

public sealed class IndexerCorePruneTests
{
    [Fact]
    public async Task ReindexAllAsync_Prunes_WithFullConfiguredSourceSet()
    {
        var scanner = new FakeSourceScanner(new[] { "shared", "career", "books" });
        var store = new PruneRecordingIndexStore();
        var core = new IndexerCore(store, scanner, new FakeEmbedder(), NullLogger.Instance);

        await core.ReindexAllAsync(CancellationToken.None);

        var call = Assert.Single(store.PruneCalls);
        Assert.Equal(new[] { "books", "career", "shared" }, call.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ReindexAllAsync_KeepsSourceThatFailedToSyncThisPass()
    {
        // "books" is CONFIGURED but its SyncAsync fails this pass (transient fetch
        // failure). It must still be in keepSources -> TombstoneSourcesNotInAsync
        // must NOT be told to prune it. Only a source REMOVED from config entirely
        // is pruned by this path.
        var scanner = new FakeSourceScanner(
            configuredSources: new[] { "shared", "books" },
            failSyncFor: new[] { "books" });
        var store = new PruneRecordingIndexStore();
        var core = new IndexerCore(store, scanner, new FakeEmbedder(), NullLogger.Instance);

        await core.ReindexAllAsync(CancellationToken.None);

        var call = Assert.Single(store.PruneCalls);
        Assert.Contains("books", call);
        Assert.Contains("shared", call);
    }

    [Fact]
    public async Task ReindexAllAsync_EmptyConfiguredSourceSet_NeverCallsPrune()
    {
        // HARD fail-safe: an empty/misparsed source config must never reach the
        // store's tombstone-everything-not-kept path.
        var scanner = new FakeSourceScanner(Array.Empty<string>());
        var store = new PruneRecordingIndexStore();
        var core = new IndexerCore(store, scanner, new FakeEmbedder(), NullLogger.Instance);

        await core.ReindexAllAsync(CancellationToken.None);

        Assert.Empty(store.PruneCalls);
    }

    [Fact]
    public async Task ReindexAllAsync_SingleRetiredSourceScenario_KeepSetExcludesIt()
    {
        // Mirrors the live incident: tenant "books" repointed from git source
        // "oreilly-library" to OPDS source "books". The retired source is no
        // longer in the scanner's configured set at all (it was removed from
        // config, not merely failing to sync) -> keepSources excludes it ->
        // TombstoneSourcesNotInAsync (called with the CURRENT config) will
        // tombstone its lingering docs.
        var scanner = new FakeSourceScanner(new[] { "books" }); // oreilly-library retired from config
        var store = new PruneRecordingIndexStore();
        var core = new IndexerCore(store, scanner, new FakeEmbedder(), NullLogger.Instance);

        await core.ReindexAllAsync(CancellationToken.None);

        var call = Assert.Single(store.PruneCalls);
        Assert.DoesNotContain("oreilly-library", call);
        Assert.Contains("books", call);
    }
}

// ── DB-gated: PostgresIndexStore.TombstoneSourcesNotInAsync against real PG ─

/// <summary>
/// Integration proof against a REAL Postgres + pgvector instance. Skipped
/// hermetically (no INDEXER_DB_HOST) via the existing <see cref="DbFactAttribute"/>
/// — same gating as <c>SearchStoreIntegrationTests</c> / <c>BookMetadataSchemaTests</c>.
/// </summary>
public sealed class PostgresTombstoneSourcesNotInTests
{
    private static Document MakeDoc(string source, string path, string body) => new()
    {
        DocId = Document.MakeDocId(source, path),
        Source = source,
        Path = path,
        Kind = "doc",
        Title = path,
        Body = body,
        ContentSha = GitSourceScanner.Sha256(body),
    };

    [DbFact]
    public async Task RetiredSource_IsTombstoned_ConfiguredSourceIsUntouched()
    {
        var store = new PostgresIndexStore(
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresIndexStore>());
        await store.EnsureSchemaAsync(CancellationToken.None);

        var suffix = Guid.NewGuid().ToString("N");
        var configuredSource = $"test-configured-{suffix}";  // e.g. "books" (OPDS)
        var retiredSource = $"test-retired-{suffix}";          // e.g. "oreilly-library" (git, repointed away)

        var keptDoc = MakeDoc(configuredSource, "kept.md", "this document belongs to a live configured source");
        var retiredDoc = MakeDoc(retiredSource, "retired.md", "this document belongs to a retired source no longer configured");

        await store.UpsertAsync(keptDoc, CancellationToken.None);
        await store.UpsertAsync(retiredDoc, CancellationToken.None);

        // Act: reindex "completed" with ONLY configuredSource in the tenant's
        // current config (retiredSource was removed from config entirely).
        var pruned = await store.TombstoneSourcesNotInAsync(new[] { configuredSource }, CancellationToken.None);

        Assert.True(pruned >= 1);

        // Retired source's doc must not surface via the read path (FTS).
        var retiredHits = await store.SearchAsync("retired source no longer configured", retiredSource, null, 10, CancellationToken.None);
        Assert.DoesNotContain(retiredHits, h => h.Path == "retired.md");

        // Configured source's doc is UNTOUCHED — still searchable.
        var keptHits = await store.SearchAsync("live configured source", configuredSource, null, 10, CancellationToken.None);
        Assert.Contains(keptHits, h => h.Path == "kept.md");
    }

    [DbFact]
    public async Task EmptyKeepSources_IsANoOp_NeverTombstonesEverything()
    {
        var store = new PostgresIndexStore(
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresIndexStore>());
        await store.EnsureSchemaAsync(CancellationToken.None);

        var suffix = Guid.NewGuid().ToString("N");
        var source = $"test-guard-{suffix}";
        var doc = MakeDoc(source, "safe.md", "this document must survive an empty keepSources call");
        await store.UpsertAsync(doc, CancellationToken.None);

        // Defence-in-depth guard AT THE STORE: an empty keepSources must be a
        // no-op, never a "tombstone everything" call — even though the primary
        // fail-safe lives in IndexerCore (the caller), the store itself must not
        // silently wipe the table if ever invoked directly with an empty set.
        var pruned = await store.TombstoneSourcesNotInAsync(Array.Empty<string>(), CancellationToken.None);
        Assert.Equal(0, pruned);

        var hits = await store.SearchAsync("survive an empty keepSources call", source, null, 10, CancellationToken.None);
        Assert.Contains(hits, h => h.Path == "safe.md");
    }

    [DbFact]
    public async Task Idempotent_SecondPruneOfSameRetiredSource_TombstonesZeroMore()
    {
        var store = new PostgresIndexStore(
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresIndexStore>());
        await store.EnsureSchemaAsync(CancellationToken.None);

        var suffix = Guid.NewGuid().ToString("N");
        var configuredSource = $"test-configured-{suffix}";
        var retiredSource = $"test-retired-idem-{suffix}";

        await store.UpsertAsync(MakeDoc(configuredSource, "kept.md", "kept content"), CancellationToken.None);
        await store.UpsertAsync(MakeDoc(retiredSource, "retired.md", "retired content"), CancellationToken.None);

        var firstPass = await store.TombstoneSourcesNotInAsync(new[] { configuredSource }, CancellationToken.None);
        Assert.True(firstPass >= 1);

        // Second reindex pass: the doc is already tombstoned (is_deleted=true),
        // so the WHERE NOT is_deleted guard means it is not re-matched.
        var secondPass = await store.TombstoneSourcesNotInAsync(new[] { configuredSource }, CancellationToken.None);
        Assert.Equal(0, secondPass);
    }
}
