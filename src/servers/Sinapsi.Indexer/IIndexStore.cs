namespace Sinapsi.Indexer;

/// <summary>
/// The index store seam — kept behind an interface so the engine stays swappable
/// (Postgres tsvector now, an SQLite-FTS5 escape hatch documented behind the same
/// contract). Both the write side (the Indexer worker: ensure-schema, idempotent
/// upsert, tombstone) and the read side (the co-hosted MCP tools) go through it.
/// </summary>
public interface IIndexStore
{
    /// <summary>Create tables/indexes if absent. Idempotent.</summary>
    Task EnsureSchemaAsync(CancellationToken ct);

    /// <summary>Upsert one document keyed by DocId. Skips the write when the
    /// stored ContentSha already matches (idempotent — no replay-doubling).
    /// Returns true when a row was inserted or updated.</summary>
    Task<bool> UpsertAsync(Document doc, CancellationToken ct);

    /// <summary>Tombstone (is_deleted = true) every doc of <paramref name="source"/>
    /// whose DocId is NOT in <paramref name="presentDocIds"/> — i.e. files that
    /// disappeared from the source since this scan. Returns the tombstoned count.</summary>
    Task<int> TombstoneMissingAsync(string source, IReadOnlyCollection<string> presentDocIds, CancellationToken ct);

    /// <summary>Tombstone (is_deleted = true) every LIVE doc whose <c>source</c>
    /// is NOT in <paramref name="keepSources"/> — i.e. a whole source that was
    /// retired from the tenant's configured source set (e.g. a git source
    /// replaced by an OPDS source, or vice versa). Idempotent (already-tombstoned
    /// rows are simply not re-matched by <c>NOT is_deleted</c>). Returns the
    /// tombstoned count.
    /// <para>
    /// CALLER FAIL-SAFE: this is a blunt, cross-source instrument — passing an
    /// empty/incomplete <paramref name="keepSources"/> would tombstone every doc
    /// NOT in that set, i.e. everything. The caller (<see cref="IndexerCore"/>)
    /// MUST refuse to call this when the configured-source set is empty; the
    /// store itself does not special-case emptiness.
    /// </para></summary>
    Task<int> TombstoneSourcesNotInAsync(IReadOnlyCollection<string> keepSources, CancellationToken ct);

    /// <summary>Liveness probe (SELECT 1). Throws on failure.</summary>
    Task PingAsync(CancellationToken ct);

    // --- read side (served by the co-hosted MCP — IndexTools) ---

    /// <summary>Unified FTS over the index (all watched sources). Optional
    /// source/kind filters; websearch query; ts_headline snippets.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, string? source, string? kind, int limit, CancellationToken ct);

    /// <summary>The learnings corpus. Optional scope bucket + optional query;
    /// lists the scope when query is null.</summary>
    Task<IReadOnlyList<LearningHit>> GetLearningsAsync(string? scope, string? query, int limit, CancellationToken ct);

    // --- hybrid (pgvector) ---

    /// <summary>Store the L2-normalised embedding for a doc.</summary>
    Task SetEmbeddingAsync(string docId, float[] vector, CancellationToken ct);

    /// <summary>Docs that still need an embedding (NULL embedding, not deleted) —
    /// drives the backfill after a re-scan / on first deploy of the vector half.</summary>
    Task<IReadOnlyList<(string DocId, string Title, string Body)>> GetMissingEmbeddingsAsync(int limit, CancellationToken ct);

    /// <summary>Hybrid search: Reciprocal-Rank-Fusion of vector (cosine) + FTS (BM25-ish)
    /// rankings. <paramref name="queryVector"/> is the embedded query.</summary>
    Task<IReadOnlyList<SearchHit>> SemanticSearchAsync(float[] queryVector, string queryText, int limit, CancellationToken ct);
}

/// <summary>One hit from <see cref="IIndexStore.SearchAsync"/>.</summary>
public sealed record SearchHit(string Source, string Path, string Kind, string Title, string Scope, string Snippet, double Score);

/// <summary>One hit from <see cref="IIndexStore.GetLearningsAsync"/>.</summary>
public sealed record LearningHit(string Path, string Title, string Scope, string Excerpt, string ContentSha, DateTimeOffset UpdatedAt);
