using ApprovalBridge.Executor.Dispatch;
using ApprovalBridge.Executor.Garmin;
using ApprovalBridge.Executor.Registry;
using ApprovalBridge.Executor.Sdk;
using Sinapsi.Nats.EventPlane;
using Xunit;

namespace ApprovalBridge.Executor.Tests;

/// <summary>
/// The generic <see cref="ExecutorDispatcher"/> is action-agnostic and deny-by-default (home-server
/// <c>docs/66 §3, §4</c>, I1/I6): every failure path — wrong command kind, missing payload, unregistered
/// action, params violating <c>param_schema</c>, no bound handler, a handler that throws — yields a REJECTED
/// ack and runs nothing.
/// </summary>
public sealed class DispatcherDenyByDefaultTests
{
    private static ExecutorDispatcher Build(params IActionExecutor[] handlers) =>
        new(Fixtures.DemoDefinitions(), new InMemoryActionExecutorRegistry(handlers),
            new RecordingSecretSourceFactory(new RecordingSecretSource(Sentinels.ClientSecret)));

    private static readonly GarminOAuthExchangeExecutor Handler =
        new(new MockGarminEndpoint(new GarminToken(Sentinels.AccessToken, Sentinels.RefreshToken, DateTimeOffset.UtcNow)),
            new MockGarminTokenStore());

    [Fact]
    public async Task WrongKind_IsRejected()
    {
        var dispatcher = Build(Handler);
        var cmd = new ActCommand(Guid.NewGuid().ToString("N"), ActCommandKind.MergePullRequest,
            "ste/x#1", "corr", "op", "reason");
        var ack = await dispatcher.DispatchAsync(cmd);
        Assert.False(ack.Accepted);
        Assert.Contains("ApprovalBridgeExecute", ack.Reason);
    }

    [Fact]
    public async Task MissingPayload_IsRejected()
    {
        var dispatcher = Build(Handler);
        var cmd = new ActCommand(Guid.NewGuid().ToString("N"), ActCommandKind.ApprovalBridgeExecute,
            "ct199-garmin", "corr", "op", "reason", Payload: null);
        var ack = await dispatcher.DispatchAsync(cmd);
        Assert.False(ack.Accepted);
        Assert.Contains("no action payload", ack.Reason);
    }

    [Fact]
    public async Task UnregisteredAction_IsRejected()
    {
        var dispatcher = Build(Handler);
        var ack = await dispatcher.DispatchAsync(Fixtures.ExecuteCommand("not.allowlisted", Fixtures.ValidParams));
        Assert.False(ack.Accepted);
        Assert.Contains("not in the executor allowlist", ack.Reason);
    }

    [Theory]
    [InlineData("""{ "auth_code": "short" }""")]                 // < minLength 8
    [InlineData("""{ "auth_code": "abcd1234", "x": 1 }""")]      // additionalProperties:false
    [InlineData("""{ }""")]                                       // required auth_code missing
    [InlineData("not json")]                                      // unparseable
    public async Task ParamsViolatingSchema_AreRejected(string badParams)
    {
        var dispatcher = Build(Handler);
        var ack = await dispatcher.DispatchAsync(Fixtures.ExecuteCommand(Fixtures.DemoActionId, badParams));
        Assert.False(ack.Accepted);
        Assert.Contains("param_schema", ack.Reason);
    }

    [Fact]
    public async Task NoBoundHandler_IsRejected()
    {
        var dispatcher = Build(); // no handlers registered
        var ack = await dispatcher.DispatchAsync(Fixtures.ExecuteCommand(Fixtures.DemoActionId, Fixtures.ValidParams));
        Assert.False(ack.Accepted);
        Assert.Contains("no executor handler", ack.Reason);
    }

    [Fact]
    public async Task HandlerThatThrows_IsRejected_WithNonSecretReason()
    {
        // A Garmin handler whose (not-provisioned) endpoint refuses → ExecutorException → deny-by-default.
        var handler = new GarminOAuthExchangeExecutor(new NotProvisionedGarminEndpoint(), new NotProvisionedGarminTokenStore());
        var dispatcher = Build(handler);
        var ack = await dispatcher.DispatchAsync(Fixtures.ExecuteCommand(Fixtures.DemoActionId, Fixtures.ValidParams));
        Assert.False(ack.Accepted);
        Assert.Contains("not provisioned", ack.Reason);
        Assert.DoesNotContain(Sentinels.ClientSecret, ack.Reason);
    }

    [Fact]
    public async Task AcceptedResult_CarriesTheActCommandResultBack()
    {
        var dispatcher = Build(Handler);
        var ack = await dispatcher.DispatchAsync(Fixtures.ExecuteCommand(Fixtures.DemoActionId, Fixtures.ValidParams));
        Assert.True(ack.Accepted);
        Assert.NotNull(ack.ResultJson);
        Assert.Contains("garmin-connector", ack.Reason); // ran under the target identity
    }
}
