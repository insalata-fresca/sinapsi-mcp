using System.Text.Json.Nodes;
using ApprovalBridge.Executor.Dispatch;
using ApprovalBridge.Executor.Garmin;
using ApprovalBridge.Executor.Registry;
using ApprovalBridge.Executor.Sdk;
using Json.Schema;
using Xunit;

namespace ApprovalBridge.Broker.Tests;

/// <summary>
/// The seal across the BROKER boundary (home-server <c>docs/66 §3.4</c>, I2): with the REAL
/// <see cref="ExecutorDispatcher"/> wired into the broker, an approved one-shot runs the Garmin executor,
/// and ONLY the non-secret <c>result_schema</c> payload flows back through the broker to the resumed agent.
/// The client secret and the exchanged token appear on NO broker-visible surface — not the approval outcome,
/// not any emitted decision fact (which carries only a params digest, never raw params or results).
/// </summary>
public sealed class BrokerExecutorSealTests
{
    private const string ClientSecret = "BROKER_SEAL__client_secret__d91f0a";
    private const string AccessToken = "BROKER_SEAL__access_token__7c3e55";
    private const string Requester = "agent:worker/session-3";
    private const string Operator = "operator:stefano";

    private const string ResultSchemaText = """
        { "type": "object", "properties": {
            "status": { "enum": ["ok", "error"] },
            "stored": { "type": "boolean" },
            "expires_at": { "type": "string", "format": "date-time" } } }
        """;

    private sealed class FixedSecret(string v) : ISecretSource
    {
        public Task<string> GetSecretAsync(string name, CancellationToken ct = default) => Task.FromResult(v);
    }
    private sealed class FixedSecretFactory(ISecretSource s) : ISecretSourceFactory
    {
        public ISecretSource ForTarget(ExecutorActionDefinition definition) => s;
    }
    private sealed class FixedEndpoint(GarminToken t) : IGarminTokenEndpoint
    {
        public Task<GarminToken> ExchangeAsync(string code, string secret, CancellationToken ct = default) => Task.FromResult(t);
    }
    private sealed class NoopStore : IGarminTokenStore
    {
        public Task StoreAsync(GarminToken token, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static ExecutorDispatcher RealExecutor()
    {
        var def = new ExecutorActionDefinition(
            ActionId: BrokerFixture.DemoActionId,
            ExecutorName: GarminOAuthExchangeExecutor.Name,
            TargetIdentity: "garmin-connector",
            ParamSchema: JsonSchema.FromText("""{ "type":"object","required":["auth_code"],"additionalProperties":false,"properties":{"auth_code":{"type":"string","minLength":8,"maxLength":512}} }"""),
            ResultSchema: JsonSchema.FromText(ResultSchemaText),
            ResultProperties: new HashSet<string>(StringComparer.Ordinal) { "status", "stored", "expires_at" });
        var token = new GarminToken(AccessToken, "refresh", DateTimeOffset.Parse("2026-09-01T12:00:00Z"));
        var handler = new GarminOAuthExchangeExecutor(new FixedEndpoint(token), new NoopStore());
        return new ExecutorDispatcher(
            new InMemoryActionDefinitionSource([def]),
            new InMemoryActionExecutorRegistry([handler]),
            new FixedSecretFactory(new FixedSecret(ClientSecret)));
    }

    [Fact]
    public async Task ApprovedAction_RunsExecutor_AndReturnsOnlyTheNonSecretResult()
    {
        var h = BrokerFixture.Build(RealExecutor());
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        var o = await h.Broker.ApproveAsync(req.RequestId, Operator);

        Assert.True(o.Accepted);
        Assert.True(o.ExecutorAccepted);          // the real executor accepted and ran
        Assert.NotNull(o.ResultJson);
        var result = JsonNode.Parse(o.ResultJson!)!.AsObject();
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.True(result["stored"]!.GetValue<bool>());
    }

    [Fact]
    public async Task NoSecretAppearsOnAnyBrokerVisibleSurface()
    {
        var h = BrokerFixture.Build(RealExecutor());
        var req = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);
        var o = await h.Broker.ApproveAsync(req.RequestId, Operator);

        // Every surface the broker exposes: the approval outcome (result + detail) …
        var brokerSurface = $"{o.ResultJson} {o.Detail} {o.Reason}";
        // … and every emitted decision fact envelope (requested / approved / executed).
        foreach (var fact in h.Emitter.Facts)
            brokerSurface += " " + fact.Envelope.ToJsonString();

        Assert.DoesNotContain(ClientSecret, brokerSurface);
        Assert.DoesNotContain(AccessToken, brokerSurface);
        // The executed fact records success WITHOUT carrying the result values (only result_status).
        var executed = h.Emitter.Facts.Single(f => f.Verdict == "executed");
        Assert.Equal("ok", executed.Envelope["result_status"]!.GetValue<string>());
    }
}
