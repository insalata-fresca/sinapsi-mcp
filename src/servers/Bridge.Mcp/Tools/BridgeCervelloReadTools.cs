using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bridge.Mcp.Audit;
using Bridge.Mcp.Auth;
using ModelContextProtocol.Server;

namespace Bridge.Mcp.Tools;

/// <summary>
/// CB-BRIDGE (design §5) — the cervello DIALOGUE READ tools, exposed to the operator's claude.ai
/// Project. Four read tools, all scope <see cref="AuthService.CervelloReadScope"/>
/// (<c>bridge:cervello:read</c>, read bucket), all thin forwarders to the CT146 dialogue backend
/// (CB-BACKEND, PR #73) behind the CT121 forwarder — the bridge stays a dumb, auth-gating,
/// content-free-logging forwarder (S50 invariant).
///
/// <list type="bullet">
/// <item><c>cervello_context_pack</c> — POST {CERVELLO_PACK_URL}/context-pack (§5.1, the core).</item>
/// <item><c>cervello_search</c>       — GET  {CERVELLO_SEARCH_URL}/search   (§5.2, indexer :8009).</item>
/// <item><c>cervello_get</c>          — GET  {CERVELLO_PACK_URL}/object     (§5.3).</item>
/// <item><c>cervello_timeline_walk</c>— GET  {CERVELLO_PACK_URL}/timeline   (§5.4).</item>
/// </list>
///
/// Fail-closed layers, per tool, BEFORE any I/O: (1) <c>CERVELLO_EXPOSED=false</c> → <c>disabled</c>;
/// (2) the relevant token empty → <c>not_configured</c>; (3) CT146/indexer 401 → <c>unauthorized</c>.
/// The CT146 response is passed through verbatim (the pack is assembled server-side); no cervello
/// content is logged to the shared bus (audit records the tool + route only, never the query).
/// HttpClients are typed + pooled (registered in Program.cs), 10s.
/// </summary>
[McpServerToolType]
public sealed class BridgeCervelloReadTools(
    AuthService auth,
    AuditService audit,
    BridgeConfig config,
    IHttpClientFactory httpClientFactory)
{
    // Distinct pooled clients: the pack/object/timeline surface (:8147) vs the search surface (:8009).
    private const string PackClientName = "cervello-pack";
    private const string SearchClientName = "cervello-search";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── 5.1 cervello_context_pack ─────────────────────────────────────────────────────────────
    [McpServerTool(Name = "cervello_context_pack")]
    [Description(
        "Assemble a bounded, ranked, SOURCED cervello context pack for a dialogue intent. " +
        "intent = goal_reasoning | portfolio | person_prep | recall | thread; focus = the goal/person/" +
        "thread slug or a free-text question. The CT146 server selects, ranks (relevance x recency x " +
        "graph-proximity), bounds to budget chars, and summarises to fit — every returned item carries " +
        "a resolvable source ref, and `coverage` states what was looked at, deferred, and the gaps you " +
        "must NOT claim beyond. Includes relevant pending open_points. Returns the pack, or " +
        "{status, note} when disabled / not configured / unreachable.")]
    public async Task<object> ContextPack(
        [Description("The dialogue intent: goal_reasoning | portfolio | person_prep | recall | thread. Required.")] string intent,
        [Description("The focus: a goal/person/thread slug or a free-text question. Optional (e.g. portfolio needs none).")] string? focus = null,
        [Description("Char budget for the pack. Omit for the server default (30000).")] int? budget = null,
        [Description("ISO-8601 baseline for the movement `delta` block (movement since this instant). Optional.")] string? since = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(AuthService.CervelloReadScope, AuthService.ReadBucket, config.RateLimitReadPerMin);
            if (DisabledEnvelope(ctx, "cervello_context_pack", out var d)) return d!;
            if (NotConfiguredEnvelope(config.EffectiveCervelloPackToken, ctx, "cervello_context_pack", out var nc)) return nc!;

            if (string.IsNullOrWhiteSpace(intent))
                throw new BridgeToolException("invalid_request", "intent must not be empty");

            var url = $"{config.CervelloPackUrl.TrimEnd('/')}/context-pack";
            var payload = JsonSerializer.Serialize(new
            {
                intent,
                focus,
                budget = budget ?? config.CervelloPackBudgetDefault,
                since,
            }, JsonOpts);

            return await ForwardAsync(
                PackClientName, HttpMethod.Post, url, config.EffectiveCervelloPackToken,
                payload, ctx, "cervello_context_pack", cancellationToken);
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── 5.2 cervello_search ───────────────────────────────────────────────────────────────────
    [McpServerTool(Name = "cervello_search")]
    [Description(
        "Hybrid full-text + semantic search over Stefano's private cervello memory (recordings, " +
        "documents, map dossiers, goals). Routes to the cervello indexer GET /search. kind filters " +
        "the object type (person | thread | goal | recording | document | all). Returns " +
        "{query, count, hits:[{title, path, snippet, rank, source, kind}]}; {status, note} on " +
        "disabled / unconfigured / unreachable.")]
    public async Task<object> Search(
        [Description("The search query. Required.")] string query,
        [Description("Object-type filter: person | thread | goal | recording | document | all. Omit for all.")] string? kind = null,
        [Description("Max hits (the indexer clamps 1–30). Omit for the server default.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(AuthService.CervelloReadScope, AuthService.ReadBucket, config.RateLimitReadPerMin);
            if (DisabledEnvelope(ctx, "cervello_search", out var d)) return d!;
            if (NotConfiguredEnvelope(config.CervelloSearchToken, ctx, "cervello_search", out var nc)) return nc!;

            if (string.IsNullOrWhiteSpace(query))
                throw new BridgeToolException("invalid_request", "query must not be empty");

            var qs = new List<string> { $"q={Uri.EscapeDataString(query)}" };
            if (!string.IsNullOrWhiteSpace(kind)) qs.Add($"kind={Uri.EscapeDataString(kind)}");
            if (limit is int l) qs.Add($"limit={l}");
            var url = $"{config.CervelloSearchUrl.TrimEnd('/')}/search?{string.Join("&", qs)}";

            return await ForwardAsync(
                SearchClientName, HttpMethod.Get, url, config.CervelloSearchToken,
                body: null, ctx, "cervello_search", cancellationToken);
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── 5.3 cervello_get ──────────────────────────────────────────────────────────────────────
    [McpServerTool(Name = "cervello_get")]
    [Description(
        "Fetch one cervello map object verbatim — a person, thread, goal, or the global timeline — " +
        "by slug. Returns its frontmatter, markdown body, and the list of source refs it cites. " +
        "Read-only; grounded. {status, note} on disabled / unconfigured / unreachable.")]
    public async Task<object> Get(
        [Description("Object kind: person | thread | goal | timeline. Required.")] string kind,
        [Description("The object slug (e.g. a goal/person/thread slug). Required.")] string id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(AuthService.CervelloReadScope, AuthService.ReadBucket, config.RateLimitReadPerMin);
            if (DisabledEnvelope(ctx, "cervello_get", out var d)) return d!;
            if (NotConfiguredEnvelope(config.EffectiveCervelloPackToken, ctx, "cervello_get", out var nc)) return nc!;

            if (string.IsNullOrWhiteSpace(kind))
                throw new BridgeToolException("invalid_request", "kind must be one of person|thread|goal|timeline");
            if (string.IsNullOrWhiteSpace(id))
                throw new BridgeToolException("invalid_request", "id must not be empty");

            var url = $"{config.CervelloPackUrl.TrimEnd('/')}/object?kind={Uri.EscapeDataString(kind)}&id={Uri.EscapeDataString(id)}";

            return await ForwardAsync(
                PackClientName, HttpMethod.Get, url, config.EffectiveCervelloPackToken,
                body: null, ctx, "cervello_get", cancellationToken);
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── 5.4 cervello_timeline_walk ─────────────────────────────────────────────────────────────
    [McpServerTool(Name = "cervello_timeline_walk")]
    [Description(
        "Walk the dated, SOURCED movement of a goal, person, or thread over a date range (how it " +
        "moved over time). anchor = goal:<slug> | person:<slug> | thread:<slug> | global. Returns " +
        "{anchor, from, to, lines:[{date, fact, links, source}]} — every line cites its evidence. " +
        "This is the DESIGN §7 timeline_walk tool, cervello-scoped. {status, note} on " +
        "disabled / unconfigured / unreachable.")]
    public async Task<object> TimelineWalk(
        [Description("The anchor: goal:<slug> | person:<slug> | thread:<slug> | global. Required.")] string anchor,
        [Description("Range start (ISO date). Optional.")] string? from = null,
        [Description("Range end (ISO date). Optional.")] string? to = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(AuthService.CervelloReadScope, AuthService.ReadBucket, config.RateLimitReadPerMin);
            if (DisabledEnvelope(ctx, "cervello_timeline_walk", out var d)) return d!;
            if (NotConfiguredEnvelope(config.EffectiveCervelloPackToken, ctx, "cervello_timeline_walk", out var nc)) return nc!;

            if (string.IsNullOrWhiteSpace(anchor))
                throw new BridgeToolException("invalid_request", "anchor must not be empty");

            var qs = new List<string> { $"anchor={Uri.EscapeDataString(anchor)}" };
            if (!string.IsNullOrWhiteSpace(from)) qs.Add($"from={Uri.EscapeDataString(from)}");
            if (!string.IsNullOrWhiteSpace(to)) qs.Add($"to={Uri.EscapeDataString(to)}");
            var url = $"{config.CervelloPackUrl.TrimEnd('/')}/timeline?{string.Join("&", qs)}";

            return await ForwardAsync(
                PackClientName, HttpMethod.Get, url, config.EffectiveCervelloPackToken,
                body: null, ctx, "cervello_timeline_walk", cancellationToken);
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── shared forwarder + envelopes (mirror BridgeOpenPointsTools) ─────────────────────────────

    private async Task<object> ForwardAsync(
        string clientName, HttpMethod method, string url, string token, string? body,
        BridgeAuthContext ctx, string tool, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(clientName);
        HttpResponseMessage response;
        try
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            response = await client.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            audit.Enqueue(tool, ctx, project: null, query: null, outcome: "unreachable");
            return new { status = "unreachable", note = $"cervello dialogue surface could not be reached: {ex.Message}" };
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return await NonOkEnvelope(response, ctx, tool, ct);

            var respBody = await response.Content.ReadAsStringAsync(ct);
            var parsed = TryParse(respBody);
            if (parsed is null)
            {
                audit.Enqueue(tool, ctx, project: null, query: null, outcome: "bad_response");
                return new { status = "bad_response", note = "cervello dialogue surface returned non-JSON" };
            }

            audit.Enqueue(tool, ctx, project: null, query: null);
            return parsed;
        }
    }

    /// <summary>ACCESS.md §7 emergency-disable — sever the tool before any I/O.</summary>
    private bool DisabledEnvelope(BridgeAuthContext ctx, string tool, out object? envelope)
    {
        if (config.CervelloExposed) { envelope = null; return false; }
        audit.Enqueue(tool, ctx, project: null, query: null, outcome: "disabled");
        envelope = new { status = "disabled", note = "cervello exposure is disabled (CERVELLO_EXPOSED=false)." };
        return true;
    }

    /// <summary>Empty effective token → not_configured before any I/O.</summary>
    private bool NotConfiguredEnvelope(string token, BridgeAuthContext ctx, string tool, out object? envelope)
    {
        if (!string.IsNullOrEmpty(token)) { envelope = null; return false; }
        audit.Enqueue(tool, ctx, project: null, query: null, outcome: "not_configured");
        envelope = new { status = "not_configured", note = "cervello dialogue surface is not configured: the bearer is unset." };
        return true;
    }

    private async Task<object> NonOkEnvelope(HttpResponseMessage response, BridgeAuthContext ctx, string tool, CancellationToken ct)
    {
        var statusCode = (int)response.StatusCode;
        var outcome = statusCode switch
        {
            400 => "bad_request",
            401 => "unauthorized",
            404 => "not_found",
            _ => "error",
        };
        audit.Enqueue(tool, ctx, project: null, query: null, outcome: outcome);
        var body = await response.Content.ReadAsStringAsync(ct);
        return new
        {
            status = outcome,
            http_status = statusCode,
            note = $"cervello dialogue surface returned HTTP {statusCode}",
            detail = TryParse(body),
        };
    }

    private static object? TryParse(string body)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(body, JsonOpts); }
        catch (JsonException) { return null; }
    }
}
