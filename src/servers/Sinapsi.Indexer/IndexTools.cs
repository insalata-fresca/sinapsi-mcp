using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Indexer;

/// <summary>
/// The MCP read surface for the index. Co-hosted in the Indexer process (it owns
/// the Postgres connection) and served over HTTP at /mcp. Full-text + hybrid
/// semantic search over the watched repos, plus a learnings-corpus lookup.
/// </summary>
[McpServerToolType]
public sealed class IndexTools
{
    [McpServerTool(Name = "search_index")]
    [Description(
        "Full-text search across the unified index of all watched repos. " +
        "Returns ranked hits with ts_headline snippets. Optional source/kind filters.")]
    public static async Task<object> SearchIndex(
        IIndexStore store,
        [Description("Full-text query (websearch syntax: words, \"phrases\", OR, -negation).")] string query,
        [Description("Optional source filter: the logical source name of a watched repo.")] string? source = null,
        [Description("Optional kind filter: doc|pattern|convention|decision|caveat|scope|state|learning|backlog")] string? kind = null,
        [Description("Max results (default 10, max 30).")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var hits = await store.SearchAsync(query, source, kind, limit, cancellationToken);
        return new { query, result_count = hits.Count, results = hits };
    }

    [McpServerTool(Name = "semantic_search")]
    [Description(
        "Hybrid semantic search over the index — fuses meaning (local " +
        "all-MiniLM-L6-v2 embeddings, pgvector cosine) with keyword FTS via " +
        "Reciprocal Rank Fusion. Use for conceptual/by-meaning queries where exact " +
        "words may differ; search_index is the keyword-only path.")]
    public static async Task<object> SemanticSearch(
        IIndexStore store,
        IEmbedder embedder,
        [Description("Natural-language query (matched by meaning + keywords).")] string query,
        [Description("Max results (default 10, max 30).")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new { error = "query is required" };
        var vec = embedder.Embed(query);
        var hits = await store.SemanticSearchAsync(vec, query, limit, cancellationToken);
        return new { query, mode = "hybrid-rrf", result_count = hits.Count, results = hits };
    }

    [McpServerTool(Name = "get_learning")]
    [Description(
        "Search the learnings corpus — the cross-project learnings store. Use BEFORE " +
        "implementing, to find prior learnings/patterns (the by-the-books pre-step).")]
    public static async Task<object> GetLearning(
        IIndexStore store,
        [Description("Optional bucket filter: \"global\", or a project slug.")] string? scope = null,
        [Description("Optional full-text query (websearch syntax); omit to list the scope.")] string? query = null,
        [Description("Max results (default 10, max 30).")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var hits = await store.GetLearningsAsync(scope, query, limit, cancellationToken);
        return new { scope, query, result_count = hits.Count, results = hits };
    }
}
