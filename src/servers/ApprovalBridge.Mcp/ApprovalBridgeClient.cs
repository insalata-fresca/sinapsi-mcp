using System.Net.Http.Json;
using System.Text.Json;

namespace ApprovalBridge.Mcp;

/// <summary>
/// Result of a call to the broker's <c>/request</c> intake (docs/66 §3.1). Never carries an
/// approval — a pending request is a fact awaiting the OPERATOR, never a green light the agent can
/// act on. <see cref="RequestId"/> is the opaque correlation handle the operator's Console/chat
/// surface will show; the agent has no way to turn it into an approval.
/// </summary>
/// <param name="Accepted">True when the broker minted a pending request and emitted
/// <c>...requested</c>. False on ANY broker refusal — unknown action, params failing
/// <c>param_schema</c>, or rate-limited (docs/66 §8 deny-by-default).</param>
/// <param name="RequestId">The pending request's correlation id (empty when refused).</param>
/// <param name="DenialReason">The broker's <c>BrokerRejectReason</c> name when refused (empty on
/// accept) — e.g. <c>UnknownAction</c>, <c>ParamsSchemaViolation</c>, <c>RateLimited</c>.</param>
public sealed record ApprovalBridgeRequestResult(bool Accepted, string RequestId, string DenialReason)
{
    public static ApprovalBridgeRequestResult Ok(string requestId) => new(true, requestId, string.Empty);
    public static ApprovalBridgeRequestResult Denied(string reason) => new(false, string.Empty, reason);
}

/// <summary>
/// The ONLY network seam this MCP server has onto the <c>ApprovalBridge.Broker</c> (E1.3). It
/// speaks to exactly one broker endpoint — <c>POST /request</c> — and intentionally has no method
/// that could ever reach <c>/approve</c> or <c>/reject</c>: docs/66 §8 T1 requires an agent be
/// STRUCTURALLY unable to approve its own request, and this client's type surface is one of the
/// two independent barriers that hold (the other is the E1.5 approve-channel authz, which scopes
/// agent identities to <c>approval_bridge_request</c> only at the gateway). A reviewer can confirm
/// "this MCP cannot approve" by reading this file alone — there is no approve/reject call to find.
/// The <c>RequestOnlyGuardTests</c> class in the test project pins this with a reflection
/// assertion over the compiled type.
/// </summary>
public sealed class ApprovalBridgeClient(HttpClient http, ApprovalBridgeOptions opt)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Submit a REQUEST to the broker's intake: <c>action_id</c> + typed <c>params</c> (already
    /// shape-validated by <see cref="ApprovalBridgeValidation"/>), under this deployment's own
    /// configured <see cref="ApprovalBridgeOptions.RequesterIdentity"/> — never a caller-supplied
    /// identity (docs/66 §3.1: "under its own (agent) identity").
    /// </summary>
    /// <exception cref="InvalidOperationException">The broker returned a 2xx response with no
    /// <c>request_id</c> (a protocol violation) — never returned as a normal denial, because it
    /// would be dishonest to tell the caller "denied" for what is actually a broken upstream
    /// contract.</exception>
    public async Task<ApprovalBridgeRequestResult> RequestAsync(
        string actionId, string paramsJson, CancellationToken ct)
    {
        var body = new
        {
            action_id = actionId,
            @params = paramsJson,
            requester_identity = opt.RequesterIdentity,
        };

        using var res = await http.PostAsJsonAsync($"{opt.BrokerBaseUrl}/request", body, _json, ct)
            .ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (res.IsSuccessStatusCode)
        {
            var requestId = TryGetString(text, "request_id");
            if (string.IsNullOrEmpty(requestId))
                throw new InvalidOperationException("broker /request: 2xx response missing request_id");
            return ApprovalBridgeRequestResult.Ok(requestId);
        }

        // Deny-by-default 422 body: { "rejected": "<BrokerRejectReason>" } (docs/66 §8). Any other
        // non-success status (network intermediary 5xx, etc.) still yields a clean denial rather
        // than throwing — the tool always returns a structured result, never an unhandled fault,
        // for the class of failure that is "the broker said no" or "something between us and the
        // broker said no on its behalf".
        var reason = TryGetString(text, "rejected");
        return ApprovalBridgeRequestResult.Denied(
            string.IsNullOrEmpty(reason) ? $"broker /request failed: HTTP {(int)res.StatusCode}" : reason);
    }

    private static string? TryGetString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
