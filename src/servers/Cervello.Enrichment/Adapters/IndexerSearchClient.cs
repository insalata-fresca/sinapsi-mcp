using System.Net.Http.Headers;
using System.Text.Json;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IIndexerSearch"/> over the cervello INDEXER's hybrid search (CT146 <c>:8009 GET
/// /search</c> — FTS + pgvector over <c>ste/cervello</c>). This is the search reuse the design
/// mandates (§2.2/§5.2): <c>cervello_search</c> forwards straight through, and the pack assembler
/// ranks recent evidence by the indexer's hybrid relevance score. Bearer-gated by a STATIC token
/// (<c>INDEXER_SEARCH_TOKEN</c>, injected agent-free at deploy) — the indexer validates by string
/// equality, so no JWT mint is needed for this route (unlike CT126/forgejo egress).
///
/// <para>Isolation: the client maps the indexer's response to the redacted <see cref="IndexerHit"/>
/// shape (title/path/snippet/rank/kind) — no transcript body, no embedding vector — so nothing raw or
/// biometric crosses this seam. The indexer clamps <c>limit</c> to 1..30 server-side.</para>
/// </summary>
public sealed class IndexerSearchClient : IIndexerSearch
{
    /// <summary>The indexer hybrid-search route (relative to the configured base URL).</summary>
    public const string RoutePath = "/search";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly ILogger _log;

    public IndexerSearchClient(HttpClient http, string searchToken, ILogger<IndexerSearchClient>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _token = searchToken ?? "";
        _log = log ?? NullLogger<IndexerSearchClient>.Instance;
    }

    public async Task<IReadOnlyList<IndexerHit>> SearchAsync(string query, string? kind, int? limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<IndexerHit>();

        var qs = $"?q={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrWhiteSpace(kind) && kind != "all") qs += $"&kind={Uri.EscapeDataString(kind)}";
        if (limit is > 0) qs += $"&limit={limit}";

        using var req = new HttpRequestMessage(HttpMethod.Get, RoutePath + qs);
        if (!string.IsNullOrEmpty(_token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // A search failure degrades gracefully — the pack still assembles from the graph, and
            // coverage.gaps surfaces the miss. Never fabricate hits.
            _log.LogWarning(e, "cervello indexer search failed for query length {Len}", query.Length);
            return Array.Empty<IndexerHit>();
        }

        if (!res.IsSuccessStatusCode)
        {
            _log.LogWarning("cervello indexer returned HTTP {Status}", (int)res.StatusCode);
            return Array.Empty<IndexerHit>();
        }

        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseHits(body);
    }

    /// <summary>
    /// Map the indexer's <c>{query, result_count, results:[{source, path, kind, title, scope, snippet,
    /// score}]}</c> (Sinapsi.Indexer <c>SearchResponse</c>) to the redacted hit shape. Tolerant of the
    /// two field-name spellings the response may use (snake / web casing).
    /// </summary>
    internal static IReadOnlyList<IndexerHit> ParseHits(string body)
    {
        var hits = new List<IndexerHit>();
        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(body, _json); }
        catch (JsonException) { return hits; }

        if (!TryGetArray(root, out var results, "results", "hits")) return hits;
        foreach (var r in results.EnumerateArray())
        {
            var title = Str(r, "title") ?? "";
            var path = Str(r, "path") ?? "";
            var snippet = Str(r, "snippet") ?? "";
            var kind = Str(r, "kind") ?? "";
            var rank = Num(r, "score", "rank") ?? 0.0;
            var date = Str(r, "date");
            // The source ref: prefer an explicit `source` ref; else the repo path is itself a valid ref.
            var source = Str(r, "source");
            if (string.IsNullOrWhiteSpace(source) || !Domain.SourceRef.IsResolvableScheme(source))
                source = string.IsNullOrWhiteSpace(path) ? title : path;
            if (string.IsNullOrWhiteSpace(source)) continue; // no source → not admissible (pack rule)
            hits.Add(new IndexerHit(title, path, snippet, rank, source!, kind, date));
        }
        return hits;
    }

    private static bool TryGetArray(JsonElement root, out JsonElement arr, params string[] names)
    {
        foreach (var n in names)
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(n, out arr) && arr.ValueKind == JsonValueKind.Array)
                return true;
        arr = default;
        return false;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static double? Num(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
        return null;
    }
}
