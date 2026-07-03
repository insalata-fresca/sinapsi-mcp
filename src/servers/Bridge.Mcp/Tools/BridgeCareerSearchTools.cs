using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using Bridge.Mcp.Audit;
using Bridge.Mcp.Auth;
using ModelContextProtocol.Server;

namespace Bridge.Mcp.Tools;

/// <summary>
/// B4b-8 CAREER_SEARCH (intentionally CHANGED tool — NOT held to Python parity).
///
/// Calls the merged Sinapsi.Indexer GET /search route (C# target: CAREER_SEARCH_URL/search).
///
/// INDEXER CONTRACT (PR #42, GET /search):
///   Request:  GET {url}/search?q={query}&amp;limit={limit}
///             Authorization: Bearer {CAREER_SEARCH_TOKEN}
///   Response 200 (camelCase JSON):
///     { query, resultCount, results: [{ source, path, kind, title, scope, snippet, score }] }
///   Response 400/500:
///     { error: "..." }
///
/// PRESERVED PYTHON FLAT HIT SHAPE (zero observable change to claude.ai):
///   hits[i] = { title, path, snippet, rank: score, source }
///   count = resultCount
///   DROP: kind, scope (not in Python hit shape)
///
/// STATUS ENVELOPES (preserve Python exactly):
///   empty CAREER_SEARCH_TOKEN → { status: "not_configured" }  (BEFORE any I/O)
///   transport failure          → { status: "unreachable" }
///   HTTP 400                   → { status: "bad_request",    http_status: 400 }
///   HTTP 401                   → { status: "unauthorized",   http_status: 401 }
///   HTTP 404                   → { status: "route_disabled", http_status: 404 }
///   HTTP other non-200         → { status: "error",          http_status: N }
///   non-JSON body              → { status: "bad_response" }
///   success                    → { query, count, hits: [...] }
///
/// HttpClient: typed + pooled (NOT new-per-call). Registered in Program.cs.
/// Timeout: 10s.
/// </summary>
[McpServerToolType]
public sealed class BridgeCareerSearchTools(
    AuthService auth,
    AuditService audit,
    BridgeConfig config,
    IHttpClientFactory httpClientFactory)
{
    // Named HttpClient registered in Program.cs as "career-search".
    private const string ClientName = "career-search";

    // Cached JsonSerializerOptions for reading indexer response.
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "career_search")]
    [Description(
        "Semantic / full-text search over Stefano's private career knowledge-map. " +
        "Routes to the merged Sinapsi Indexer GET /search. " +
        "Returns {query, count, hits:[{title, path, snippet, rank, source}]} on success. " +
        "Returns {status, note, http_status?} on failure. " +
        "If CAREER_SEARCH_TOKEN is unset, returns {status:'not_configured'} immediately.")]
    public async Task<object> CareerSearch(
        [Description("Search query string. Must not be empty.")] string query,
        [Description("Max results to return (default 10, indexer clamps 1–30).")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(
                AuthService.ReadDocumentsScope,
                AuthService.ReadBucket,
                config.RateLimitReadPerMin);

            if (string.IsNullOrWhiteSpace(query))
                throw new BridgeToolException("invalid_query", "query must not be empty");

            // If CAREER_SEARCH_TOKEN is empty → not_configured BEFORE any I/O.
            if (string.IsNullOrEmpty(config.CareerSearchToken))
            {
                audit.Enqueue("career_search", ctx, project: null, query: query, outcome: "not_configured");
                return new
                {
                    status = "not_configured",
                    note   = "career_search is not configured: CAREER_SEARCH_TOKEN is unset. " +
                             "Set it from Vaultwarden 'homelab/sinapsi-indexer/career-search-token'.",
                };
            }

            // Build the request URL.
            // CAREER_SEARCH_URL is the base URL (e.g. http://indexer.internal:8009).
            // The indexer exposes GET /search?q=&limit=.
            var baseUrl = config.CareerSearchUrl.TrimEnd('/');
            var requestUrl = $"{baseUrl}/search?q={Uri.EscapeDataString(query)}&limit={limit}";

            var client = httpClientFactory.CreateClient(ClientName);

            HttpResponseMessage response;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                req.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.CareerSearchToken);
                response = await client.SendAsync(req, cancellationToken);
            }
            catch (Exception ex)
            {
                audit.Enqueue("career_search", ctx, project: null, query: query, outcome: "unreachable");
                return new
                {
                    status = "unreachable",
                    note   = $"career indexer could not be reached: {ex.Message}",
                };
            }

            using (response)
            {
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    var statusCode = (int)response.StatusCode;
                    var outcome = statusCode switch
                    {
                        400 => "bad_request",
                        401 => "unauthorized",
                        404 => "route_disabled",
                        _   => "error",
                    };
                    audit.Enqueue("career_search", ctx, project: null, query: query, outcome: outcome);
                    return new
                    {
                        status      = outcome,
                        http_status = statusCode,
                        note        = $"career indexer returned HTTP {statusCode}",
                    };
                }

                // Parse response body.
                string body;
                try
                {
                    body = await response.Content.ReadAsStringAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    audit.Enqueue("career_search", ctx, project: null, query: query, outcome: "bad_response");
                    return new
                    {
                        status = "bad_response",
                        note   = $"career indexer returned unreadable body: {ex.Message}",
                    };
                }

                IndexerSearchResponse? data;
                try
                {
                    data = JsonSerializer.Deserialize<IndexerSearchResponse>(body, JsonOpts);
                }
                catch (JsonException)
                {
                    audit.Enqueue("career_search", ctx, project: null, query: query, outcome: "bad_response");
                    return new
                    {
                        status = "bad_response",
                        note   = "career indexer returned non-JSON",
                    };
                }

                if (data is null)
                {
                    audit.Enqueue("career_search", ctx, project: null, query: query, outcome: "bad_response");
                    return new
                    {
                        status = "bad_response",
                        note   = "career indexer returned null response",
                    };
                }

                // MAP indexer camelCase SearchResponse → preserved Python flat hit shape:
                //   hits[i] = { title, path, snippet, rank: score, source }
                //   count = resultCount
                //   DROP: kind, scope (not in Python hit shape)
                var hits = (data.Results ?? []).Select(r => (object)new
                {
                    title   = r.Title,
                    path    = r.Path,
                    snippet = r.Snippet,
                    rank    = r.Score,    // score → rank
                    source  = r.Source,
                    // kind + scope are DROPPED (zero observable change to claude.ai)
                }).ToList();

                audit.Enqueue("career_search", ctx, project: null, query: query);
                return new
                {
                    query = data.Query ?? query,
                    count = data.ResultCount,
                    hits,
                };
            }
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── Indexer response DTOs (camelCase, matching PR #42 contract) ───────────

    /// <summary>
    /// Indexer GET /search 200 response.
    /// Python field → C# property (JsonPropertyName not needed with JsonSerializerDefaults.Web).
    /// camelCase: query, resultCount, results[].
    /// </summary>
    private sealed class IndexerSearchResponse
    {
        public string? Query       { get; init; }
        public int     ResultCount { get; init; }
        public List<IndexerSearchResult>? Results { get; init; }
    }

    /// <summary>
    /// One result from the indexer (PR #42: source, path, kind, title, scope, snippet, score).
    /// We map: score → rank, drop kind + scope.
    /// </summary>
    private sealed class IndexerSearchResult
    {
        public string? Source  { get; init; }
        public string? Path    { get; init; }
        public string? Kind    { get; init; }   // dropped in output
        public string? Title   { get; init; }
        public string? Scope   { get; init; }   // dropped in output
        public string? Snippet { get; init; }
        public double  Score   { get; init; }   // → rank
    }
}
