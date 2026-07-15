namespace ApprovalBridge.Mcp;

/// <summary>
/// Env-driven configuration for the agent-facing Operator Approval Bridge REQUEST tool
/// (home-server <c>docs/66-operator-approval-bridge.md</c> §3.1, mission E1.6). This server
/// exposes ONLY the request path — see <see cref="ApprovalBridgeTools"/> and
/// <see cref="ApprovalBridgeClient"/>, which structurally carry no approve/reject call.
///
/// <para>
/// <see cref="RequesterIdentity"/> is deliberately a DEPLOY-TIME value, never a caller-supplied
/// tool argument: docs/66 §3.1 requires the agent to call the tool "under its own (agent)
/// identity", and docs/66 §8 T1 relies on <c>requester_identity</c> being an honest anchor for
/// the broker's <c>approver_identity != requester_identity</c> check and for the provenance the
/// Console renders to the operator (§8 T4). If an agent could pass an arbitrary
/// <c>requester_identity</c> string as a tool argument it could impersonate another agent's
/// provenance; binding it to the deployment (like the per-target <c>identity</c> in the E1.1
/// allowlist, or the per-agent JWK in <c>Sinapsi.AgentJwt</c>) keeps it truthful.
/// </para>
/// </summary>
public sealed record ApprovalBridgeOptions
{
    /// <summary>Base URL of the <c>ApprovalBridge.Broker</c> (E1.3) — e.g.
    /// <c>http://approval-bridge-broker:8013</c>. No default: no broker instance is baked into
    /// the image.</summary>
    public required string BrokerBaseUrl { get; init; }

    /// <summary>This deployment's own agent identity, e.g. <c>agent:cervello-worker/ct139</c>.
    /// Sent as <c>requester_identity</c> on every request — never taken from the tool call.</summary>
    public required string RequesterIdentity { get; init; }

    /// <summary>Bounds every call to the broker's <c>/request</c> endpoint.</summary>
    public int HttpTimeoutMs { get; init; } = DefaultHttpTimeoutMs;

    /// <summary>Default HTTP timeout (ms) when none is configured.</summary>
    internal const int DefaultHttpTimeoutMs = 30_000;

    /// <summary>Upper bound on a configurable HTTP timeout (ms). 10 minutes is far past any
    /// legitimate broker call; a larger value is treated as a config error, not honoured.</summary>
    internal const int MaxHttpTimeoutMs = 600_000;

    public static ApprovalBridgeOptions FromEnvironment() => new()
    {
        BrokerBaseUrl = RequiredBrokerBaseUrl(),
        RequesterIdentity = EnvRequired("APPROVAL_BRIDGE_REQUESTER_IDENTITY"),
        HttpTimeoutMs = ReadHttpTimeoutMs(),
    };

    private static string Env(string k, string def) =>
        Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : def;

    private static string EnvRequired(string k) =>
        Environment.GetEnvironmentVariable(k) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException(
                $"{k} is required (inject it via the deploy env, e.g. /etc/approval-bridge-mcp/approval-bridge.env)");

    private static string RequiredBrokerBaseUrl()
    {
        var url = Environment.GetEnvironmentVariable("APPROVAL_BRIDGE_BROKER_URL");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                "APPROVAL_BRIDGE_BROKER_URL is required (no broker host is baked into the image); " +
                "supply it via the deploy env (e.g. /etc/approval-bridge-mcp/approval-bridge.env)");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                $"APPROVAL_BRIDGE_BROKER_URL must be an absolute http(s) URL; got '{url}'.");
        return url.TrimEnd('/');
    }

    /// <summary>
    /// Read + fail-closed-validate the HTTP timeout. A value of <c>0</c> would make every
    /// request time out instantly and a negative value throws inside the
    /// <see cref="HttpClient"/> ctor; any value <c>&lt;= 0</c> or above
    /// <see cref="MaxHttpTimeoutMs"/> is rejected as invalid config — we throw a clear error
    /// naming the offending env var rather than silently honouring a footgun.
    /// </summary>
    private static int ReadHttpTimeoutMs()
    {
        var raw = Environment.GetEnvironmentVariable("APPROVAL_BRIDGE_HTTP_TIMEOUT_MS");
        if (string.IsNullOrEmpty(raw))
            return DefaultHttpTimeoutMs;

        if (!int.TryParse(raw, out var ms) || ms <= 0 || ms > MaxHttpTimeoutMs)
            throw new InvalidOperationException(
                $"APPROVAL_BRIDGE_HTTP_TIMEOUT_MS='{raw}' is invalid: expected an integer in " +
                $"1..{MaxHttpTimeoutMs} ms (default {DefaultHttpTimeoutMs}).");

        return ms;
    }
}
