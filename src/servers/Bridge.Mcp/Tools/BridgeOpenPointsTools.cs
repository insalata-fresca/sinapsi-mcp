using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bridge.Mcp.Audit;
using Bridge.Mcp.Auth;
using ModelContextProtocol.Server;

namespace Bridge.Mcp.Tools;

/// <summary>
/// S50 L3 — the cervello OPEN-POINTS Surface A tools, exposed to the operator's claude.ai app
/// (web/mobile — his ONLY enrichment UI). Mirrors the M5 <c>career_search</c> exposure pattern:
/// each tool authorizes on the bridge side (scope + rate limit) then calls the token-gated cervello
/// open-points HTTP surface on CT146 (routed via the CT121-mcp-gateway PEP, ACCESS.md §8) with a
/// cervello-scoped bearer.
///
/// <para>Two tools:</para>
/// <list type="bullet">
/// <item><c>cervello_open_points_list</c> — read (<see cref="AuthService.CervelloReadScope"/>): the
///   operator's pending open-points, REDACTED (refs + question + scored candidates only — R10).</item>
/// <item><c>cervello_open_points_answer</c> — deposit (<see cref="AuthService.CervelloDepositScope"/>,
///   because answering WRITES BACK): applies the confirmed fact with a <c>human://</c> basis, updates
///   the glossary, and enrolls/refines the voiceprint. A dismiss omits the fact (never guessed).</item>
/// </list>
///
/// <para>Fail-closed layers: (1) <c>CERVELLO_EXPOSED=false</c> severs both tools at the edge before
/// any I/O (ACCESS.md §7 emergency-disable); (2) an empty <c>CERVELLO_OPEN_POINTS_TOKEN</c> →
/// <c>not_configured</c> before any I/O; (3) the CT146 gate itself refuses a missing/invalid bearer
/// (401 → <c>unauthorized</c>). Cervello content never leaves via a shared surface — the response is
/// the redacted view the CT146 surface returns; no cervello data is logged to the shared bus.</para>
///
/// <para>HttpClient: typed + pooled (registered in Program.cs as "cervello-open-points"), 10s.</para>
/// </summary>
[McpServerToolType]
public sealed class BridgeOpenPointsTools(
    AuthService auth,
    AuditService audit,
    BridgeConfig config,
    IHttpClientFactory httpClientFactory)
{
    private const string ClientName = "cervello-open-points";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "cervello_open_points_list")]
    [Description(
        "List the operator's PENDING cervello open-points — the escalated attribution / correction / " +
        "link questions the enrichment engine could not answer with sufficient evidence, awaiting a " +
        "human decision. Each entry is REDACTED: a ref (rec://, bundle://), a one-line question, and " +
        "scored candidate answers — no transcript, audio, or biometric. Optional filters: kind " +
        "(speaker|correction|link) and recording. Returns {count, open_points:[{point_id, kind, " +
        "recording, bundle, question, candidates:[{value, confidence, why}]}]} on success, or " +
        "{status, note} when disabled / not configured / unreachable.")]
    public async Task<object> ListOpenPoints(
        [Description("Optional kind filter: speaker | correction | link. Omit for all.")] string? kind = null,
        [Description("Optional recording id filter (rec://<id> or the bare id). Omit for all.")] string? recording = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(
                AuthService.CervelloReadScope,
                AuthService.ReadBucket,
                config.RateLimitReadPerMin);

            if (DisabledEnvelope(ctx, "cervello_open_points_list", out var disabled))
                return disabled!;
            if (NotConfiguredEnvelope(ctx, "cervello_open_points_list", out var notConfigured))
                return notConfigured!;

            var baseUrl = config.CervelloOpenPointsUrl.TrimEnd('/');
            var query = BuildListQuery(kind, recording);
            var requestUrl = $"{baseUrl}/open-points{query}";

            var client = httpClientFactory.CreateClient(ClientName);
            HttpResponseMessage response;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.CervelloOpenPointsToken);
                response = await client.SendAsync(req, cancellationToken);
            }
            catch (Exception ex)
            {
                audit.Enqueue("cervello_open_points_list", ctx, project: null, query: null, outcome: "unreachable");
                return new { status = "unreachable", note = $"cervello open-points surface could not be reached: {ex.Message}" };
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    return await NonOkEnvelope(response, ctx, "cervello_open_points_list", cancellationToken);

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                object? parsed = TryParse(body);
                if (parsed is null)
                {
                    audit.Enqueue("cervello_open_points_list", ctx, project: null, query: null, outcome: "bad_response");
                    return new { status = "bad_response", note = "cervello open-points surface returned non-JSON" };
                }

                audit.Enqueue("cervello_open_points_list", ctx, project: null, query: null);
                return parsed;
            }
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    [McpServerTool(Name = "cervello_open_points_answer")]
    [Description(
        "ANSWER a cervello open-point — the learning signal. mode='select' confirms one of the point's " +
        "candidate values; mode='value' supplies a free value the candidates did not offer; " +
        "mode='dismiss' omits the fact (never guessed) and records the dismissal. A resolving answer " +
        "APPLIES the confirmed fact with a human:// basis (opens a map review-PR — dry-run by default), " +
        "updates the glossary for a correction, and enrolls/refines the speaker's voiceprint (if " +
        "consented). Idempotent: answering an already-resolved point is a no-op. Returns {point_id, " +
        "status, kind?, basis?, pr_branch?, enrolled, glossary_updated}.")]
    public async Task<object> AnswerOpenPoint(
        [Description("The open-point id to answer (e.g. op_...). Required.")] string pointId,
        [Description("Answer mode: select | value | dismiss. Required.")] string mode,
        [Description("The confirmed value for a select/value answer. Omit for dismiss.")] string? value = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Answering writes back → gate on the deposit scope + bucket (ACCESS.md §3/§4).
            var ctx = auth.Authorize(
                AuthService.CervelloDepositScope,
                AuthService.DepositBucket,
                config.RateLimitDepositPerMin);

            if (DisabledEnvelope(ctx, "cervello_open_points_answer", out var disabled))
                return disabled!;
            if (NotConfiguredEnvelope(ctx, "cervello_open_points_answer", out var notConfigured))
                return notConfigured!;

            if (string.IsNullOrWhiteSpace(pointId))
                throw new BridgeToolException("invalid_request", "pointId must not be empty");
            if (string.IsNullOrWhiteSpace(mode))
                throw new BridgeToolException("invalid_request", "mode must be one of select|value|dismiss");

            var baseUrl = config.CervelloOpenPointsUrl.TrimEnd('/');
            var requestUrl = $"{baseUrl}/open-points/{Uri.EscapeDataString(pointId)}/answer";
            var payload = JsonSerializer.Serialize(new { mode, value }, JsonOpts);

            var client = httpClientFactory.CreateClient(ClientName);
            HttpResponseMessage response;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, requestUrl)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.CervelloOpenPointsToken);
                response = await client.SendAsync(req, cancellationToken);
            }
            catch (Exception ex)
            {
                audit.Enqueue("cervello_open_points_answer", ctx, project: null, query: pointId, outcome: "unreachable");
                return new { status = "unreachable", note = $"cervello open-points surface could not be reached: {ex.Message}" };
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    return await NonOkEnvelope(response, ctx, "cervello_open_points_answer", cancellationToken, pointId);

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                object? parsed = TryParse(body);
                if (parsed is null)
                {
                    audit.Enqueue("cervello_open_points_answer", ctx, project: null, query: pointId, outcome: "bad_response");
                    return new { status = "bad_response", note = "cervello open-points surface returned non-JSON" };
                }

                audit.Enqueue("cervello_open_points_answer", ctx, project: null, query: pointId);
                return parsed;
            }
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>ACCESS.md §7 emergency-disable — sever the tools before any I/O.</summary>
    private bool DisabledEnvelope(BridgeAuthContext ctx, string tool, out object? envelope)
    {
        if (config.CervelloExposed)
        {
            envelope = null;
            return false;
        }
        audit.Enqueue(tool, ctx, project: null, query: null, outcome: "disabled");
        envelope = new
        {
            status = "disabled",
            note = "cervello exposure is disabled (CERVELLO_EXPOSED=false). Surface A is severed.",
        };
        return true;
    }

    /// <summary>Empty CERVELLO_OPEN_POINTS_TOKEN → not_configured before any I/O.</summary>
    private bool NotConfiguredEnvelope(BridgeAuthContext ctx, string tool, out object? envelope)
    {
        if (!string.IsNullOrEmpty(config.CervelloOpenPointsToken))
        {
            envelope = null;
            return false;
        }
        audit.Enqueue(tool, ctx, project: null, query: null, outcome: "not_configured");
        envelope = new
        {
            status = "not_configured",
            note = "cervello open-points is not configured: CERVELLO_OPEN_POINTS_TOKEN is unset.",
        };
        return true;
    }

    private async Task<object> NonOkEnvelope(
        HttpResponseMessage response, BridgeAuthContext ctx, string tool, CancellationToken ct, string? query = null)
    {
        var statusCode = (int)response.StatusCode;
        var outcome = statusCode switch
        {
            400 => "bad_request",
            401 => "unauthorized",
            404 => "not_found",
            _ => "error",
        };
        audit.Enqueue(tool, ctx, project: null, query: query, outcome: outcome);

        // Pass through the surface's JSON error body when present (it carries a helpful note).
        var body = await response.Content.ReadAsStringAsync(ct);
        var parsed = TryParse(body);
        return new
        {
            status = outcome,
            http_status = statusCode,
            note = $"cervello open-points surface returned HTTP {statusCode}",
            detail = parsed,
        };
    }

    private static string BuildListQuery(string? kind, string? recording)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(kind))
            parts.Add($"kind={Uri.EscapeDataString(kind)}");
        if (!string.IsNullOrWhiteSpace(recording))
            parts.Add($"recording={Uri.EscapeDataString(recording)}");
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    private static object? TryParse(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(body, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
