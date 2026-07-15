using System.ComponentModel;
using System.Text.Json;
using Bridge.Mcp.Audit;
using Bridge.Mcp.Auth;
using ModelContextProtocol.Server;
using Sinapsi.AgentJwt;
using Sinapsi.Mcp;

namespace Bridge.Mcp.Tools;

/// <summary>
/// Personal-health READ tools, exposed to the operator's claude.ai Project. Thin 1:1 proxies
/// over two internal read-only MCP backends that live BEHIND the CT121-mcp-gateway PEP:
/// <list type="bullet">
///   <item><b>health-mcp</b> (CT121 :9226, Google Health API v4 — Withings + Garmin + phone,
///     aggregated via Health Connect): <c>health_list_weight</c> / <c>_list_sleep</c> /
///     <c>_list_steps</c> / <c>_list_datapoints</c> / <c>_list_data_types</c>.</item>
///   <item><b>withings-mcp</b> (CT121 :9227, Withings Public Health Data API): <c>withings_list_weight</c>
///     / <c>_list_body_composition</c> / <c>_list_measures</c> / <c>_list_measure_types</c>.</item>
/// </list>
///
/// <para><b>Transport + identity (mirrors <c>SageCouncil.Mcp</c>, NOT the cervello REST-bearer path).</b>
/// The health/withings backends are MCP servers reached ONLY as MCP <c>tools/call</c> through the
/// CT121 agentgateway PEP (<c>GATEWAY_URL</c>, default <c>http://127.0.0.1:8443/mcp</c>). Each call
/// mints the bridge's scoped agent identity (<c>BRIDGE_HEALTH_AGENT</c>) as a short-lived RFC 7523
/// JWT via the in-repo <see cref="AgentJwtMinter"/> (<c>Sinapsi.AgentJwt</c>), and forwards the tool
/// through the in-repo <see cref="GatewayMcpClient"/> (<c>Sinapsi.Mcp</c>). No token is hardcoded; the
/// JWK is loaded at call time from <c>AGENT_KEY_DIR</c>. The PEP authorizes the call against the
/// agent's grant (agentgateway CEL + OpenFGA); the backend tool names are prefixed with the backend
/// alias by the gateway, so the wire tool name IS <c>health_*</c> / <c>withings_*</c>.</para>
///
/// <para>Fail-closed layers, per tool, BEFORE any I/O: (1) <c>HEALTH_EXPOSED=false</c> →
/// <c>disabled</c>; (2) <c>BRIDGE_HEALTH_AGENT</c> empty → <c>not_configured</c>. A PEP DENY
/// (401/403) surfaces as <c>unauthorized</c>; an unreachable gateway as <c>unreachable</c>. All
/// tools are <see cref="AuthService.HealthReadScope"/> (<c>bridge:health:read</c>, read bucket).
/// The backend response text (already JSON) is passed through verbatim; the audit records the tool
/// + outcome only, never the query window.</para>
/// </summary>
[McpServerToolType]
public sealed class BridgeHealthTools(
    AuthService auth,
    AuditService audit,
    BridgeConfig config,
    GatewayMcpClient gateway,
    AgentJwtMinter jwtMinter)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── health-mcp (Google Health API v4) ──────────────────────────────────────────────────────

    [McpServerTool(Name = "health_list_weight")]
    [Description(
        "List body-weight data points from Google Health (Withings + Garmin + phone, aggregated via " +
        "Health Connect). Read-only. Optional ISO-8601 start/end bound the window (e.g. " +
        "2026-07-01T00:00:00Z); omit for no bound. Returns {dataType, count, dataPoints}, or " +
        "{status, note} when disabled / not configured / unauthorized / unreachable.")]
    public Task<object> HealthListWeight(
        [Description("Optional ISO-8601 window start. Omit for no start bound.")] string? start = null,
        [Description("Optional ISO-8601 window end. Omit for no end bound.")] string? end = null,
        CancellationToken cancellationToken = default)
        => CallAsync("health_list_weight", StartEnd(start, end), cancellationToken);

    [McpServerTool(Name = "health_list_sleep")]
    [Description(
        "List sleep data points from Google Health (Garmin + phone). Read-only. Optional ISO-8601 " +
        "start/end bound the window; omit for no bound. Returns {dataType, count, dataPoints}, or " +
        "{status, note} when disabled / not configured / unauthorized / unreachable.")]
    public Task<object> HealthListSleep(
        [Description("Optional ISO-8601 window start. Omit for no start bound.")] string? start = null,
        [Description("Optional ISO-8601 window end. Omit for no end bound.")] string? end = null,
        CancellationToken cancellationToken = default)
        => CallAsync("health_list_sleep", StartEnd(start, end), cancellationToken);

    [McpServerTool(Name = "health_list_steps")]
    [Description(
        "List step-count data points from Google Health (Garmin + phone). Read-only. Optional " +
        "ISO-8601 start/end bound the window; omit for no bound. Returns {dataType, count, " +
        "dataPoints}, or {status, note} when disabled / not configured / unauthorized / unreachable.")]
    public Task<object> HealthListSteps(
        [Description("Optional ISO-8601 window start. Omit for no start bound.")] string? start = null,
        [Description("Optional ISO-8601 window end. Omit for no end bound.")] string? end = null,
        CancellationToken cancellationToken = default)
        => CallAsync("health_list_steps", StartEnd(start, end), cancellationToken);

    [McpServerTool(Name = "health_list_datapoints")]
    [Description(
        "Generic read of Google Health API v4 data points for ANY data type. Read-only. dataType " +
        "examples: weight, sleep, steps (verified), heart_rate / activity types (coverage TBC). " +
        "Optional ISO-8601 start/end bound the window; omit for no bound. Prefer the typed tools " +
        "(health_list_weight/sleep/steps) for the common cases. {status, note} on disabled / " +
        "not configured / unauthorized / unreachable.")]
    public Task<object> HealthListDatapoints(
        [Description("Google Health data type, e.g. weight, sleep, steps, heart_rate. Required.")] string dataType,
        [Description("Optional ISO-8601 window start. Omit for no start bound.")] string? start = null,
        [Description("Optional ISO-8601 window end. Omit for no end bound.")] string? end = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataType))
            return InvalidRequest("health_list_datapoints", "dataType must not be empty");
        var args = StartEnd(start, end);
        args["dataType"] = dataType;
        return CallAsync("health_list_datapoints", args, cancellationToken);
    }

    [McpServerTool(Name = "health_list_data_types")]
    [Description(
        "List the Google Health data types this MCP is configured to advertise. Read-only, no " +
        "upstream call on the backend. Returns {data_types:[...]}, or {status, note} when disabled " +
        "/ not configured / unauthorized / unreachable.")]
    public Task<object> HealthListDataTypes(CancellationToken cancellationToken = default)
        => CallAsync("health_list_data_types", new Dictionary<string, object>(StringComparer.Ordinal), cancellationToken);

    // ── withings-mcp (Withings Public Health Data API) ──────────────────────────────────────────

    [McpServerTool(Name = "withings_list_weight")]
    [Description(
        "List body-weight measurements from Withings (meastype 1, kg). Read-only. Optional start/end " +
        "(ISO-8601 or a UNIX epoch-seconds integer) bound the window; omit for no bound. Each measure's " +
        "real value = value x 10^unit. Returns {meastypes, measuregrp_count, body}, or {status, note} " +
        "when disabled / not configured / unauthorized / unreachable.")]
    public Task<object> WithingsListWeight(
        [Description("Optional window start (ISO-8601 or unix seconds). Omit for no start bound.")] string? start = null,
        [Description("Optional window end (ISO-8601 or unix seconds). Omit for no end bound.")] string? end = null,
        CancellationToken cancellationToken = default)
        => CallAsync("withings_list_weight", StartEnd(start, end), cancellationToken);

    [McpServerTool(Name = "withings_list_body_composition")]
    [Description(
        "List body-composition measurements from Withings in one call: weight (1), fat ratio (6), " +
        "fat mass weight (8), muscle mass (76), bone mass (88), hydration (77). Read-only. Optional " +
        "start/end (ISO-8601 or unix seconds) bound the window; omit for no bound. Each measure's real " +
        "value = value x 10^unit. {status, note} on disabled / not configured / unauthorized / unreachable.")]
    public Task<object> WithingsListBodyComposition(
        [Description("Optional window start (ISO-8601 or unix seconds). Omit for no start bound.")] string? start = null,
        [Description("Optional window end (ISO-8601 or unix seconds). Omit for no end bound.")] string? end = null,
        CancellationToken cancellationToken = default)
        => CallAsync("withings_list_body_composition", StartEnd(start, end), cancellationToken);

    [McpServerTool(Name = "withings_list_measures")]
    [Description(
        "Generic Withings getmeas read for ANY meastype code(s). Read-only. meastypes is a " +
        "comma-separated list of numeric codes (e.g. \"1,6,76\") or names from " +
        "withings_list_measure_types (e.g. \"weight,fat_ratio\"). Optional start/end (ISO-8601 or " +
        "unix seconds) bound the window; omit for no bound. Prefer the typed tools for the common " +
        "cases. {status, note} on disabled / not configured / unauthorized / unreachable.")]
    public Task<object> WithingsListMeasures(
        [Description("Comma-separated meastype codes or names, e.g. \"1,6,76\" or \"weight,muscle_mass\". Required.")] string meastypes,
        [Description("Optional window start (ISO-8601 or unix seconds). Omit for no start bound.")] string? start = null,
        [Description("Optional window end (ISO-8601 or unix seconds). Omit for no end bound.")] string? end = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(meastypes))
            return InvalidRequest("withings_list_measures", "meastypes must not be empty");
        var args = StartEnd(start, end);
        args["meastypes"] = meastypes;
        return CallAsync("withings_list_measures", args, cancellationToken);
    }

    [McpServerTool(Name = "withings_list_measure_types")]
    [Description(
        "List the Withings meastype codes this MCP knows by name (weight=1, fat_ratio=6, " +
        "fat_free_mass=5, fat_mass_weight=8, muscle_mass=76, bone_mass=88, hydration=77). Read-only, " +
        "no upstream call on the backend. {status, note} on disabled / not configured / unauthorized " +
        "/ unreachable.")]
    public Task<object> WithingsListMeasureTypes(CancellationToken cancellationToken = default)
        => CallAsync("withings_list_measure_types", new Dictionary<string, object>(StringComparer.Ordinal), cancellationToken);

    // ── shared forwarder + envelopes ────────────────────────────────────────────────────────────

    /// <summary>Build the common {start?, end?} argument bag (only non-empty values are sent).</summary>
    private static Dictionary<string, object> StartEnd(string? start, string? end)
    {
        var args = new Dictionary<string, object>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(start)) args["start"] = start;
        if (!string.IsNullOrWhiteSpace(end)) args["end"] = end;
        return args;
    }

    /// <summary>
    /// Authorize → fail-closed edge checks → mint the bridge agent JWT → call the backend tool
    /// through the CT121 PEP → pass the (JSON) result through verbatim. Never throws to the SDK for
    /// a backend/PEP problem: returns a {status, note} envelope so claude.ai gets a graceful signal.
    /// Auth/rate-limit failures DO throw (via BridgeToolError) to match the other bridge tools.
    /// </summary>
    private async Task<object> CallAsync(string tool, Dictionary<string, object> args, CancellationToken ct)
    {
        BridgeAuthContext ctx;
        try
        {
            ctx = auth.Authorize(AuthService.HealthReadScope, AuthService.ReadBucket, config.RateLimitReadPerMin);
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }

        if (!config.HealthExposed)
        {
            audit.Enqueue(tool, ctx, project: null, query: null, outcome: "disabled");
            return new { status = "disabled", note = "health exposure is disabled (HEALTH_EXPOSED=false)." };
        }
        if (string.IsNullOrWhiteSpace(config.HealthAgent))
        {
            audit.Enqueue(tool, ctx, project: null, query: null, outcome: "not_configured");
            return new { status = "not_configured", note = "the bridge health agent identity is not configured (BRIDGE_HEALTH_AGENT unset)." };
        }

        string jwt;
        try
        {
            jwt = await jwtMinter.MintAsync(config.HealthAgent, ct);
        }
        catch (Exception ex)
        {
            audit.Enqueue(tool, ctx, project: null, query: null, outcome: "mint_failed");
            return new { status = "not_configured", note = $"could not mint the bridge health agent identity: {ex.Message}" };
        }

        string text;
        try
        {
            text = await gateway.CallToolAsync(new Uri(config.GatewayUrl), jwt, tool, args, ct);
        }
        catch (Exception ex)
        {
            var msg = ex.Message ?? "";
            // GatewayMcpClient surfaces a non-2xx as "tools/call: <code> ..." / "initialize: <code> ...".
            var unauthorized = msg.Contains(" 401", StringComparison.Ordinal) || msg.Contains(" 403", StringComparison.Ordinal);
            var outcome = unauthorized ? "unauthorized" : "unreachable";
            audit.Enqueue(tool, ctx, project: null, query: null, outcome: outcome);
            return unauthorized
                ? new { status = "unauthorized", note = $"the CT121 gateway denied the bridge agent for {tool} (grant required): {msg}" }
                : new { status = "unreachable", note = $"the health/withings backend could not be reached via the CT121 gateway: {msg}" };
        }

        var parsed = TryParse(text);
        if (parsed is null)
        {
            audit.Enqueue(tool, ctx, project: null, query: null, outcome: "bad_response");
            return new { status = "bad_response", note = "the health/withings backend returned non-JSON", raw = text };
        }

        audit.Enqueue(tool, ctx, project: null, query: null);
        return parsed;
    }

    private Task<object> InvalidRequest(string tool, string message)
    {
        // Mirror the other tools: an invalid request is a BridgeToolException surfaced via the SDK.
        try { throw new BridgeToolException("invalid_request", message); }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    private static object? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(body, JsonOpts); }
        catch (JsonException) { return null; }
    }
}
