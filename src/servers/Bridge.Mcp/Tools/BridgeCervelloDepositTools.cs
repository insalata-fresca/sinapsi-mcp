using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bridge.Mcp.Audit;
using Bridge.Mcp.Auth;
using ModelContextProtocol.Server;

namespace Bridge.Mcp.Tools;

/// <summary>
/// CB-BRIDGE (design §5) — the cervello DIALOGUE DEPOSIT tools (the capture loop). Three write-back
/// tools, all scope <see cref="AuthService.CervelloDepositScope"/> (<c>bridge:cervello:deposit</c>,
/// deposit bucket, 30/min), all thin forwarders to the CT146 capture backend (CB-BACKEND, PR #73)
/// behind the CT121 forwarder. The bridge stays a dumb, auth-gating, content-free-logging forwarder;
/// all provenance / review-PR / lint / pin-on-cite work lives server-side on CT146.
///
/// <list type="bullet">
/// <item><c>cervello_capture_fact</c>  — POST {CERVELLO_CAPTURE_URL}/capture (§5.5). Confirm-by-
///   default; deposits a candidate into conversations/ + inbox/, NEVER map/ (the E1 graph-add human
///   gate stands).</item>
/// <item><c>cervello_set_goal</c>       — POST {CERVELLO_CAPTURE_URL}/goal (§5.6). Writes/updates
///   map/goals/&lt;slug&gt;.md via a lint-checked review-PR (dry-run by default); never deletes a
///   prior grounded line (INGEST §5).</item>
/// <item><c>cervello_link_evidence</c>  — POST {CERVELLO_CAPTURE_URL}/goal/{slug}/evidence (§5.7).
///   Appends a sourced `## Movimento` line via review-PR; external refs pinned on cite (R11).</item>
/// </list>
///
/// Fail-closed layers, per tool, BEFORE any I/O: CERVELLO_EXPOSED=false → disabled; token empty →
/// not_configured; CT146 401 → unauthorized. confirm=false previews (no write); confirm=true opens
/// the review-PR. The deposit guard (cervello paths only) is enforced server-side on CT146.
/// </summary>
[McpServerToolType]
public sealed class BridgeCervelloDepositTools(
    AuthService auth,
    AuditService audit,
    BridgeConfig config,
    IHttpClientFactory httpClientFactory)
{
    private const string ClientName = "cervello-capture";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── 5.5 cervello_capture_fact ──────────────────────────────────────────────────────────────
    [McpServerTool(Name = "cervello_capture_fact")]
    [Description(
        "Capture a fact the operator states in chat into cervello — grounded and sourced, NEVER " +
        "silently merged into the map. Deposits a candidate into conversations/ + inbox/ where the " +
        "ingestion spine reviews it through the human graph-add gate. source_hint records provenance " +
        "(source: deposit://<id> + your stated basis). relates_to attaches it to a person/thread/goal. " +
        "With confirm=false returns a preview of exactly what will be written and where; with " +
        "confirm=true commits it. Returns {status, deposit_id, path, commit, will_enter, basis}, or " +
        "{status, note} on disabled / unconfigured / unreachable.")]
    public async Task<object> CaptureFact(
        [Description("The fact to capture (a plain sentence the operator stated). Required.")] string fact,
        [Description("Provenance hint, e.g. \"said by X on 2026-07-08\". Optional.")] string? sourceHint = null,
        [Description("Attach to entities: [\"person:<slug>\",\"thread:<slug>\",\"goal:<slug>\"]. Optional.")] string[]? relatesTo = null,
        [Description("false (default) → preview only, no write; true → deposit the candidate bundle.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(AuthService.CervelloDepositScope, AuthService.DepositBucket, config.RateLimitDepositPerMin);
            if (DisabledEnvelope(ctx, "cervello_capture_fact", out var d)) return d!;
            if (NotConfiguredEnvelope(ctx, "cervello_capture_fact", out var nc)) return nc!;

            if (string.IsNullOrWhiteSpace(fact))
                throw new BridgeToolException("invalid_request", "fact must not be empty");

            var url = $"{config.CervelloCaptureUrl.TrimEnd('/')}/capture";
            var payload = JsonSerializer.Serialize(new
            {
                fact,
                source_hint = sourceHint,
                relates_to = relatesTo,
                confirm,
            }, JsonOpts);

            return await ForwardAsync(url, payload, ctx, "cervello_capture_fact", cancellationToken);
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── 5.6 cervello_set_goal ──────────────────────────────────────────────────────────────────
    [McpServerTool(Name = "cervello_set_goal")]
    [Description(
        "Create or update a tracked cervello goal object (map/goals/<slug>.md). A goal has an " +
        "objective, a status, an evidence timeline (## Movimento), and next steps — mirroring a thread " +
        "dossier. Writes via a lint-checked review-PR (dry-run by default). confirm=false previews the " +
        "dossier; confirm=true opens the PR. Never deletes a prior grounded line. status = active | " +
        "achieved | stalled | dropped | paused. Returns {status, goal_slug, pr_branch?, path, basis}, " +
        "or {status, note} on disabled / unconfigured / unreachable.")]
    public async Task<object> SetGoal(
        [Description("The goal name. Required.")] string name,
        [Description("Status: active | achieved | stalled | dropped | paused. Optional.")] string? status = null,
        [Description("Time horizon (free text, e.g. \"Q3\"). Optional.")] string? horizon = null,
        [Description("What \"done\" looks like / why it matters. Optional.")] string? objective = null,
        [Description("Linked people slugs. Optional.")] string[]? people = null,
        [Description("Linked thread slugs. Optional.")] string[]? threads = null,
        [Description("Tags. Optional.")] string[]? tags = null,
        [Description("Next steps (decisions & next). Optional.")] string[]? nextSteps = null,
        [Description("Provenance hint for this goal edit. Optional.")] string? sourceHint = null,
        [Description("false (default) → preview the dossier, no write; true → open the review-PR.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(AuthService.CervelloDepositScope, AuthService.DepositBucket, config.RateLimitDepositPerMin);
            if (DisabledEnvelope(ctx, "cervello_set_goal", out var d)) return d!;
            if (NotConfiguredEnvelope(ctx, "cervello_set_goal", out var nc)) return nc!;

            if (string.IsNullOrWhiteSpace(name))
                throw new BridgeToolException("invalid_request", "name must not be empty");

            var url = $"{config.CervelloCaptureUrl.TrimEnd('/')}/goal";
            var payload = JsonSerializer.Serialize(new
            {
                name,
                status,
                horizon,
                objective,
                people,
                threads,
                tags,
                next_steps = nextSteps,
                source_hint = sourceHint,
                confirm,
            }, JsonOpts);

            return await ForwardAsync(url, payload, ctx, "cervello_set_goal", cancellationToken);
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── 5.7 cervello_link_evidence ─────────────────────────────────────────────────────────────
    [McpServerTool(Name = "cervello_link_evidence")]
    [Description(
        "Attach a piece of evidence (a recording segment, a pinned document, an enrichment bundle) to " +
        "a tracked goal as a sourced ## Movimento line — this is how a goal 'moves'. External refs " +
        "(drive://, gmail://) are pinned on cite. Writes via a lint-checked review-PR (dry-run by " +
        "default). confirm=false previews; confirm=true opens the PR. Returns " +
        "{status, goal_slug, line, source, pr_branch?, basis}, or {status, note} on " +
        "disabled / unconfigured / unreachable.")]
    public async Task<object> LinkEvidence(
        [Description("The target goal slug. Required.")] string goalSlug,
        [Description("The evidence ref: rec://… | pin://… | drive://… | bundle://…. Required.")] string evidenceRef,
        [Description("The one-line fact this evidence supports. Required.")] string fact,
        [Description("The date of the evidence (ISO date). Optional; defaults server-side.")] string? date = null,
        [Description("false (default) → preview the Movimento line, no write; true → open the review-PR.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(AuthService.CervelloDepositScope, AuthService.DepositBucket, config.RateLimitDepositPerMin);
            if (DisabledEnvelope(ctx, "cervello_link_evidence", out var d)) return d!;
            if (NotConfiguredEnvelope(ctx, "cervello_link_evidence", out var nc)) return nc!;

            if (string.IsNullOrWhiteSpace(goalSlug))
                throw new BridgeToolException("invalid_request", "goalSlug must not be empty");
            if (string.IsNullOrWhiteSpace(evidenceRef))
                throw new BridgeToolException("invalid_request", "evidenceRef must not be empty");
            if (string.IsNullOrWhiteSpace(fact))
                throw new BridgeToolException("invalid_request", "fact must not be empty");

            var url = $"{config.CervelloCaptureUrl.TrimEnd('/')}/goal/{Uri.EscapeDataString(goalSlug)}/evidence";
            var payload = JsonSerializer.Serialize(new
            {
                goal_slug = goalSlug,
                evidence_ref = evidenceRef,
                fact,
                date,
                confirm,
            }, JsonOpts);

            return await ForwardAsync(url, payload, ctx, "cervello_link_evidence", cancellationToken);
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── shared forwarder + envelopes ───────────────────────────────────────────────────────────

    private async Task<object> ForwardAsync(
        string url, string payload, BridgeAuthContext ctx, string tool, CancellationToken ct)
    {
        var token = config.EffectiveCervelloPackToken;
        var client = httpClientFactory.CreateClient(ClientName);
        HttpResponseMessage response;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            response = await client.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            audit.Enqueue(tool, ctx, project: null, query: null, outcome: "unreachable");
            return new { status = "unreachable", note = $"cervello capture surface could not be reached: {ex.Message}" };
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return await NonOkEnvelope(response, ctx, tool, ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            var parsed = TryParse(body);
            if (parsed is null)
            {
                audit.Enqueue(tool, ctx, project: null, query: null, outcome: "bad_response");
                return new { status = "bad_response", note = "cervello capture surface returned non-JSON" };
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

    /// <summary>Empty effective pack token → not_configured before any I/O.</summary>
    private bool NotConfiguredEnvelope(BridgeAuthContext ctx, string tool, out object? envelope)
    {
        if (!string.IsNullOrEmpty(config.EffectiveCervelloPackToken)) { envelope = null; return false; }
        audit.Enqueue(tool, ctx, project: null, query: null, outcome: "not_configured");
        envelope = new { status = "not_configured", note = "cervello capture surface is not configured: the bearer is unset." };
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
            note = $"cervello capture surface returned HTTP {statusCode}",
            detail = TryParse(body),
        };
    }

    private static object? TryParse(string body)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(body, JsonOpts); }
        catch (JsonException) { return null; }
    }
}
