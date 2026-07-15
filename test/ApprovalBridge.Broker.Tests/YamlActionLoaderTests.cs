using ApprovalBridge.Broker.Core;
using ApprovalBridge.Broker.Events;
using ApprovalBridge.Broker.Model;
using ApprovalBridge.Broker.Registry;
using ApprovalBridge.Broker.Store;
using Sinapsi.Nats.EventPlane;
using Xunit;

namespace ApprovalBridge.Broker.Tests;

/// <summary>
/// The broker loads the E1.1 git-backed allowlist YAML read-only. This exercises the loader against the
/// exact shape of <c>policies/approval-bridge/actions/garmin.oauth.exchange.yaml</c>, and proves the
/// loaded spec's <c>param_schema</c> then drives real intake validation.
/// </summary>
public sealed class YamlActionLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("approval-bridge-actions").FullName;

    private const string GarminYaml = """
        action_id: garmin.oauth.exchange
        title: "Garmin OAuth code->token exchange"
        description: >
          Exchange a Garmin OAuth authorization code for a token and store it server-side.
        target:
          host: ct199-garmin
          identity: garmin-connector
        executor: garmin-oauth-exchange
        param_schema:
          type: object
          required: [auth_code]
          additionalProperties: false
          properties:
            auth_code:
              type: string
              minLength: 8
              maxLength: 512
        result_schema:
          type: object
          properties:
            status:
              enum: [ok, error]
        risk_tier: yellow
        expiry_seconds: 300
        rate_limit:
          per_agent_per_hour: 3
          per_action_per_hour: 10
        one_shot: true
        """;

    [Fact]
    public void LoadsTheAllowlistEntry_WithTypedFields()
    {
        File.WriteAllText(Path.Combine(_dir, "garmin.oauth.exchange.yaml"), GarminYaml);
        var registry = YamlActionLoader.LoadDirectory(_dir);

        var spec = registry.Find("garmin.oauth.exchange");
        Assert.NotNull(spec);
        Assert.Equal("ct199-garmin", spec!.TargetHost);
        Assert.Equal("garmin-connector", spec.TargetIdentity);
        Assert.Equal(300, spec.ExpirySeconds);           // parsed as int, not "300"
        Assert.Equal(3, spec.RateLimit.PerAgentPerHour);
        Assert.True(spec.OneShot);
    }

    [Fact]
    public void FilenameStem_MustEqualActionId()
    {
        File.WriteAllText(Path.Combine(_dir, "wrong-name.yaml"), GarminYaml);
        Assert.Throws<InvalidDataException>(() => YamlActionLoader.LoadDirectory(_dir));
    }

    [Fact]
    public async Task LoadedParamSchema_DrivesIntakeValidation()
    {
        File.WriteAllText(Path.Combine(_dir, "garmin.oauth.exchange.yaml"), GarminYaml);
        var registry = YamlActionLoader.LoadDirectory(_dir);
        var broker = new BridgeBroker(registry, new InMemoryApprovalStore(), new RecordingEmitter(),
            new NullActCommandDispatcher(), new InMemoryRateLimiter(), TimeProvider.System, new CryptoNonceSource());

        Assert.True((await broker.RequestAsync("garmin.oauth.exchange", """{"auth_code":"abcd1234"}""", "agent:x")).Accepted);
        var bad = await broker.RequestAsync("garmin.oauth.exchange", """{"auth_code":"x"}""", "agent:x"); // minLength 8
        Assert.False(bad.Accepted);
        Assert.Equal(BrokerRejectReason.ParamsSchemaViolation, bad.Reason);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}
