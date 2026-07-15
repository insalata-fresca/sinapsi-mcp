using System.Text.Json;
using System.Text.Json.Nodes;
using ApprovalBridge.Executor.Sdk;

namespace ApprovalBridge.Executor.Garmin;

/// <summary>
/// The DEMO executor for <c>garmin.oauth.exchange</c> (home-server <c>docs/66 §6</c>) — the smallest handler
/// that exercises the seal end-to-end. It:
/// <list type="number">
///   <item>takes the agent-supplied, non-secret <c>auth_code</c> from the validated params;</item>
///   <item>reads the Garmin client secret <b>target-side</b> via Path D (<see cref="ISecretSource"/>) —
///     the secret is materialised only inside this method, under the target identity;</item>
///   <item>exchanges code→token against the (mock-in-tests) <see cref="IGarminTokenEndpoint"/>;</item>
///   <item>stores the token SERVER-SIDE via <see cref="IGarminTokenStore"/>;</item>
///   <item>returns ONLY <c>{status, stored, expires_at}</c> — never the client secret, never the token.</item>
/// </list>
/// Nothing about this handler is special to the SDK: it is one registered <see cref="IActionExecutor"/>. The
/// seal is enforced structurally by the <see cref="Dispatch.ExecutorDispatcher"/> re-validating this result
/// against <c>result_schema</c>, but the handler is also written to never emit a secret in the first place.
/// </summary>
public sealed class GarminOAuthExchangeExecutor : IActionExecutor
{
    /// <summary>The allowlist <c>executor:</c> name this handler binds to (garmin.oauth.exchange.yaml).</summary>
    public const string Name = "garmin-oauth-exchange";

    /// <summary>The env/Infisical key of the Garmin client secret, read via Path D target-side.</summary>
    public const string ClientSecretName = "GARMIN_OAUTH_CLIENT_SECRET";

    private readonly IGarminTokenEndpoint _endpoint;
    private readonly IGarminTokenStore _store;

    public GarminOAuthExchangeExecutor(IGarminTokenEndpoint endpoint, IGarminTokenStore store)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string ExecutorName => Name;

    public async Task<ExecutorResult> ExecuteAsync(ExecutorRequest request, ISecretSource secrets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(secrets);

        var authCode = ReadAuthCode(request.ParamsJson);

        // ── Path D: the ONLY point the client secret exists. It stays in this local; it is never logged,
        //    never put in an exception, never returned. ──────────────────────────────────────────────────
        var clientSecret = await secrets.GetSecretAsync(ClientSecretName, ct);
        if (string.IsNullOrEmpty(clientSecret))
            throw new ExecutorException("client secret unavailable target-side");

        // Exchange + persist server-side. The token, like the secret, never leaves the target.
        var token = await _endpoint.ExchangeAsync(authCode, clientSecret, ct);
        await _store.StoreAsync(token, ct);

        // Return ONLY the non-secret confirmation conforming to result_schema.
        var result = new JsonObject
        {
            ["status"] = "ok",
            ["stored"] = true,
            ["expires_at"] = token.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        };
        return ExecutorResult.Ok(result.ToJsonString());
    }

    private static string ReadAuthCode(string paramsJson)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(paramsJson); }
        catch (JsonException) { throw new ExecutorException("params were not valid JSON"); }
        var code = node?["auth_code"]?.GetValue<string>();
        if (string.IsNullOrEmpty(code))
            throw new ExecutorException("auth_code missing from params");
        return code;
    }
}
