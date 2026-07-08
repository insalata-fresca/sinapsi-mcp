namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port over the cervello INDEXER's hybrid search (CT146 <c>:8009 GET /search</c> — FTS + pgvector
/// over <c>ste/cervello</c>). This is the search reuse the design mandates (§2.2/§5.2): the pack
/// assembler ranks recent evidence by the indexer's hybrid-search relevance score, and
/// <c>cervello_search</c> forwards straight through. The live adapter calls the indexer HTTP route;
/// a fake returns a scripted ranked set for offline tests (no network, no personal data).
///
/// <para>Isolation: the adapter returns only the redacted hit shape (title/path/snippet/rank/kind) —
/// no transcript body, no embedding — so nothing biometric or raw ever crosses this seam.</para>
/// </summary>
public interface IIndexerSearch
{
    /// <summary>
    /// Hybrid FTS+semantic search. <paramref name="kind"/> filters the object type (person|thread|
    /// goal|recording|document|all); <paramref name="limit"/> is clamped by the indexer to 1..30.
    /// Returns ranked hits, highest relevance first.
    /// </summary>
    Task<IReadOnlyList<IndexerHit>> SearchAsync(string query, string? kind, int? limit, CancellationToken ct = default);
}

/// <summary>
/// One ranked hit (design §5.2 response item). <c>Source</c> is the resolvable ref the pack item will
/// carry; <c>Rank</c> is the indexer's hybrid-search relevance score (used by the pack ranker).
/// <c>Date</c> (optional, ISO or YYYY-MM-DD) lets the pack ranker apply recency weighting.
/// </summary>
public sealed record IndexerHit(
    string Title,
    string Path,
    string Snippet,
    double Rank,
    string Source,
    string Kind,
    string? Date = null);
