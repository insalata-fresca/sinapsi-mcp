using System.Text.Json.Nodes;
using ApprovalBridge.Executor.Dispatch;
using ApprovalBridge.Executor.Garmin;
using Xunit;

namespace ApprovalBridge.Executor.Tests;

/// <summary>
/// THE SEAL (home-server <c>docs/66 §3/§4</c>, invariant I2). Proves that when the executor runs the
/// approved <c>garmin.oauth.exchange</c> action, the client secret is read ONLY target-side (via Path D) and
/// the client secret AND the exchanged token NEVER appear in the result returned to the broker/agent, nor in
/// any broker/agent-visible surface of the ack. The secret is read exactly once, on the target, under the
/// target's own identity — everything the coordinator ever sees is the non-secret <c>result_schema</c> payload.
/// </summary>
public sealed class SealProofTests
{
    private static (ExecutorDispatcher Dispatcher, RecordingSecretSource Secret, RecordingSecretSourceFactory Factory,
        MockGarminEndpoint Endpoint, MockGarminTokenStore Store) BuildGarminExecutor()
    {
        var secret = new RecordingSecretSource(Sentinels.ClientSecret);
        var factory = new RecordingSecretSourceFactory(secret);
        var token = new GarminToken(Sentinels.AccessToken, Sentinels.RefreshToken, DateTimeOffset.Parse("2026-09-01T12:00:00Z"));
        var endpoint = new MockGarminEndpoint(token);
        var store = new MockGarminTokenStore();
        var handler = new GarminOAuthExchangeExecutor(endpoint, store);
        var registry = new InMemoryActionExecutorRegistry([handler]);
        var dispatcher = new ExecutorDispatcher(Fixtures.DemoDefinitions(), registry, factory);
        return (dispatcher, secret, factory, endpoint, store);
    }

    [Fact]
    public async Task Executed_ReturnsOnlyTheNonSecretResult_NeverTheSecretOrToken()
    {
        var (dispatcher, _, _, _, _) = BuildGarminExecutor();
        var cmd = Fixtures.ExecuteCommand(Fixtures.DemoActionId, Fixtures.ValidParams);

        var ack = await dispatcher.DispatchAsync(cmd);

        Assert.True(ack.Accepted);
        Assert.NotNull(ack.ResultJson);

        // The result is exactly the non-secret confirmation shape.
        var result = JsonNode.Parse(ack.ResultJson!)!.AsObject();
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.True(result["stored"]!.GetValue<bool>());
        Assert.Equal("2026-09-01T12:00:00Z", result["expires_at"]!.GetValue<string>());
        Assert.False(result.ContainsKey("token"));         // no token field at all
        Assert.False(result.ContainsKey("access_token"));
        Assert.False(result.ContainsKey("client_secret"));

        // THE SEAL: no secret material anywhere on the surface the broker/agent receives (result + reason).
        var brokerVisible = ack.ResultJson + " " + ack.Reason;
        Assert.DoesNotContain(Sentinels.ClientSecret, brokerVisible);
        Assert.DoesNotContain(Sentinels.AccessToken, brokerVisible);
        Assert.DoesNotContain(Sentinels.RefreshToken, brokerVisible);
    }

    [Fact]
    public async Task Secret_IsReadExactlyOnce_TargetSide_UnderTheTargetIdentity()
    {
        var (dispatcher, secret, factory, endpoint, store) = BuildGarminExecutor();
        var cmd = Fixtures.ExecuteCommand(Fixtures.DemoActionId, Fixtures.ValidParams);

        await dispatcher.DispatchAsync(cmd);

        // Read exactly once, and only the client secret — nothing else was materialised.
        Assert.Equal([GarminOAuthExchangeExecutor.ClientSecretName], secret.Reads);
        // The secret source was scoped to the TARGET's own identity (I2), not the requester's.
        Assert.Equal([Fixtures.TargetIdentity], factory.ScopedTo);
        // The exchange genuinely used the target-side secret (proves it wasn't dispatched by the broker).
        Assert.Equal(Sentinels.ClientSecret, endpoint.SeenClientSecret);
        Assert.Equal(Sentinels.AuthCode, endpoint.SeenAuthCode);
        // The token was persisted SERVER-SIDE, never returned.
        var stored = Assert.Single(store.Stored);
        Assert.Equal(Sentinels.AccessToken, stored.AccessToken);
    }

    [Fact]
    public void DispatchCommand_CarriesNoSecret_OnlyActionIdAndNonSecretParams()
    {
        // The inbound command the broker dispatches must itself be free of any secret — it carries only the
        // action_id and the schema-validated, non-secret auth_code (the seal starts at dispatch).
        var cmd = Fixtures.ExecuteCommand(Fixtures.DemoActionId, Fixtures.ValidParams);
        var wholeCommand = $"{cmd.CommandId}{cmd.Target}{cmd.CorrelationId}{cmd.RequestedBy}{cmd.Reason}" +
                           $"{cmd.Payload!.ActionId}{cmd.Payload!.ParamsJson}";
        Assert.DoesNotContain(Sentinels.ClientSecret, wholeCommand);
        Assert.DoesNotContain(Sentinels.AccessToken, wholeCommand);
        Assert.Contains(Sentinels.AuthCode, wholeCommand);  // the non-secret param is legitimately present
    }

    [Fact]
    public async Task LeakyHandler_IsRefused_BeforeItsResultCanReachTheBroker()
    {
        // A mis-authored handler that tries to return a token is refused by the result_schema gate —
        // deny-by-default — so the leak never reaches the broker/agent surface.
        var secret = new RecordingSecretSource(Sentinels.ClientSecret);
        var factory = new RecordingSecretSourceFactory(secret);
        var registry = new InMemoryActionExecutorRegistry([new LeakyExecutor(Sentinels.AccessToken)]);
        var dispatcher = new ExecutorDispatcher(Fixtures.DemoDefinitions(), registry, factory);

        var ack = await dispatcher.DispatchAsync(Fixtures.ExecuteCommand(Fixtures.DemoActionId, Fixtures.ValidParams));

        Assert.False(ack.Accepted);
        Assert.Null(ack.ResultJson);                              // nothing carried back
        Assert.Contains("undeclared field 'token'", ack.Reason); // caught by the declared-keys whitelist
        Assert.DoesNotContain(Sentinels.AccessToken, ack.Reason); // and the leaked value is not in the reason
    }
}
