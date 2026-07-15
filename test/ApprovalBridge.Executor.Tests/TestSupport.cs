using ApprovalBridge.Executor.Dispatch;
using ApprovalBridge.Executor.Garmin;
using ApprovalBridge.Executor.Registry;
using ApprovalBridge.Executor.Sdk;
using Json.Schema;
using Sinapsi.Nats.EventPlane;

namespace ApprovalBridge.Executor.Tests;

/// <summary>Sentinels used across the seal tests. If ANY of these strings appears in a broker/agent-visible
/// surface, the seal is broken.</summary>
internal static class Sentinels
{
    public const string ClientSecret = "GARMIN_CLIENT_SECRET__must_never_leak__9f83aa";
    public const string AccessToken = "ACCESS_TOKEN__must_never_leak__c71d20";
    public const string RefreshToken = "REFRESH_TOKEN__must_never_leak__b40e11";
    public const string AuthCode = "authcode_abcd1234"; // agent-supplied, non-secret
}

/// <summary>A Path-D secret source that hands back a sentinel secret and RECORDS every read, so a test can
/// prove the secret was read exactly once, target-side, and nowhere else.</summary>
internal sealed class RecordingSecretSource(string value) : ISecretSource
{
    public List<string> Reads { get; } = [];
    public string TargetIdentityTag { get; init; } = "";
    public Task<string> GetSecretAsync(string name, CancellationToken ct = default)
    {
        Reads.Add(name);
        return Task.FromResult(value);
    }
}

/// <summary>A secret-source factory that returns a single shared <see cref="RecordingSecretSource"/> and
/// records which target identity it was asked to scope to (proving I2 — the target's own identity).</summary>
internal sealed class RecordingSecretSourceFactory(RecordingSecretSource source) : ISecretSourceFactory
{
    public List<string> ScopedTo { get; } = [];
    public ISecretSource ForTarget(ExecutorActionDefinition definition)
    {
        ScopedTo.Add(definition.TargetIdentity);
        return source;
    }
}

/// <summary>A mock Garmin token endpoint — no live network. It asserts the client secret it received is the
/// real one (so the exchange genuinely used the target-side secret) and returns sentinel tokens.</summary>
internal sealed class MockGarminEndpoint(GarminToken token) : IGarminTokenEndpoint
{
    public string? SeenClientSecret { get; private set; }
    public string? SeenAuthCode { get; private set; }
    public Task<GarminToken> ExchangeAsync(string authCode, string clientSecret, CancellationToken ct = default)
    {
        SeenAuthCode = authCode;
        SeenClientSecret = clientSecret;
        return Task.FromResult(token);
    }
}

/// <summary>A mock token store — records that a token was stored server-side (never returned).</summary>
internal sealed class MockGarminTokenStore : IGarminTokenStore
{
    public List<GarminToken> Stored { get; } = [];
    public Task StoreAsync(GarminToken token, CancellationToken ct = default)
    {
        Stored.Add(token);
        return Task.CompletedTask;
    }
}

/// <summary>A handler that deliberately tries to LEAK a secret through its result — used to prove the
/// dispatcher's result_schema gate refuses it (deny-by-default) before it can reach the broker.</summary>
internal sealed class LeakyExecutor(string leak) : IActionExecutor
{
    public string ExecutorName => GarminOAuthExchangeExecutor.Name;
    public Task<ExecutorResult> ExecuteAsync(ExecutorRequest request, ISecretSource secrets, CancellationToken ct = default)
        // Smuggles a token under an undeclared "token" key WITH an otherwise-valid status — this passes the
        // (open-additionalProperties) result_schema but is caught by the dispatcher's declared-keys whitelist.
        => Task.FromResult(ExecutorResult.Ok($$"""{"status":"ok","token":"{{leak}}"}"""));
}

internal static class Fixtures
{
    public const string DemoActionId = "garmin.oauth.exchange";
    public const string TargetIdentity = "garmin-connector";

    private const string ParamSchemaText = """
        { "type": "object", "required": ["auth_code"], "additionalProperties": false,
          "properties": { "auth_code": { "type": "string", "minLength": 8, "maxLength": 512 } } }
        """;

    // result_schema from the real allowlist entry: status enum + stored bool + expires_at date-time.
    private const string ResultSchemaText = """
        { "type": "object", "properties": {
            "status": { "enum": ["ok", "error"] },
            "stored": { "type": "boolean" },
            "expires_at": { "type": "string", "format": "date-time" } } }
        """;

    public static ExecutorActionDefinition DemoDefinition() => new(
        ActionId: DemoActionId,
        ExecutorName: GarminOAuthExchangeExecutor.Name,
        TargetIdentity: TargetIdentity,
        ParamSchema: JsonSchema.FromText(ParamSchemaText),
        ResultSchema: JsonSchema.FromText(ResultSchemaText),
        ResultProperties: new HashSet<string>(StringComparer.Ordinal) { "status", "stored", "expires_at" });

    public static InMemoryActionDefinitionSource DemoDefinitions() => new([DemoDefinition()]);

    public static string ValidParams => $$"""{ "auth_code": "{{Sentinels.AuthCode}}" }""";

    /// <summary>Build an ApprovalBridgeExecute command carrying the given non-secret payload — exactly what
    /// the broker dispatches after an approved one-shot.</summary>
    public static ActCommand ExecuteCommand(string actionId, string paramsJson) => new(
        CommandId: Guid.NewGuid().ToString("N"),
        Kind: ActCommandKind.ApprovalBridgeExecute,
        Target: "ct199-garmin",
        CorrelationId: Guid.NewGuid().ToString("N"),
        RequestedBy: "operator:stefano",
        Reason: "approved; one-shot nonce consumed",
        Payload: new ActPayload(actionId, paramsJson));

    /// <summary>The fixtures dir copied next to the test assembly (the real allowlist YAML).</summary>
    public static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "fixtures");
}
