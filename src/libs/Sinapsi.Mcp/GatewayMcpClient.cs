using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Sinapsi.Mcp;

/// <summary>
/// Minimal client that speaks MCP JSON-RPC (Streamable HTTP) to an upstream
/// MCP endpoint: one <c>initialize</c> → <c>notifications/initialized</c> →
/// <c>tools/call</c> round-trip, parsing either an SSE <c>data:</c> frame or a
/// plain JSON body.
/// </summary>
public sealed class GatewayMcpClient(HttpClient http)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Call a single tool on the upstream endpoint as a given bearer identity.
    /// Returns the concatenated text content of the tool result.
    /// </summary>
    public async Task<string> CallToolAsync(
        Uri gateway,
        string bearerJwt,
        string toolName,
        object toolArgs,
        CancellationToken ct)
    {
        // Validate the caller-supplied inputs at the seam: an absolute http(s)
        // gateway, a present control-char-free bearer identity, and a present
        // control-char-free tool name. Malformed values are rejected here with a
        // named ArgumentException instead of failing opaquely inside HttpClient or
        // corrupting an outbound header.
        gateway = McpValidation.RequireGateway(gateway);
        bearerJwt = McpValidation.RequireBearer(bearerJwt);
        toolName = McpValidation.RequireToolName(toolName);

        // Step 1 — initialize, capture Mcp-Session-Id.
        using var initReq = _post(gateway, bearerJwt, sessionId: null, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "sinapsi-mcp", version = "0.1.0" },
            },
        });
        using var initRes = await http.SendAsync(initReq, ct).ConfigureAwait(false);
        if (!initRes.IsSuccessStatusCode)
        {
            var body = await initRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"initialize: {(int)initRes.StatusCode} {McpSanitizer.Sanitize(body)}");
        }

        var sessionId = initRes.Headers.TryGetValues("mcp-session-id", out var vals)
            ? vals.FirstOrDefault()
            : null;
        if (string.IsNullOrEmpty(sessionId))
            throw new InvalidOperationException("upstream did not return mcp-session-id");
        _ = await initRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false); // drain

        // Step 2 — notifications/initialized.
        using (var notifReq = _post(gateway, bearerJwt, sessionId, new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized",
        }))
        using (await http.SendAsync(notifReq, ct).ConfigureAwait(false)) { }

        // Step 3 — tools/call.
        using var callReq = _post(gateway, bearerJwt, sessionId, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new { name = toolName, arguments = toolArgs },
        });
        using var callRes = await http.SendAsync(callReq, ct).ConfigureAwait(false);
        var text = await callRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!callRes.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"tools/call: {(int)callRes.StatusCode} {McpSanitizer.Sanitize(text)}");

        return _extractContent(text);
    }

    private static HttpRequestMessage _post(Uri gateway, string jwt, string? sessionId, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, gateway)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (!string.IsNullOrEmpty(sessionId))
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        return req;
    }

    private static string _extractContent(string bodyText)
    {
        JsonElement payload = default;
        var found = false;

        // SSE-style: first `data: {...}` line.
        foreach (var raw in bodyText.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            try
            {
                payload = JsonDocument.Parse(line[5..].Trim()).RootElement.Clone();
                found = true;
                break;
            }
            catch (JsonException) { /* keep scanning */ }
        }

        if (!found)
        {
            try { payload = JsonDocument.Parse(bodyText).RootElement.Clone(); found = true; }
            catch (JsonException)
            {
                throw new InvalidOperationException(
                    "unparseable response: " +
                    McpSanitizer.Sanitize(bodyText[..Math.Min(200, bodyText.Length)]));
            }
        }

        if (payload.TryGetProperty("error", out var err))
            throw new InvalidOperationException($"tool error: {McpSanitizer.Sanitize(err.GetRawText())}");

        if (payload.TryGetProperty("result", out var result) &&
            result.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var c in content.EnumerateArray())
                if (c.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.AppendLine(t.GetString());
            return sb.ToString().TrimEnd('\n');
        }

        return string.Empty;
    }
}
