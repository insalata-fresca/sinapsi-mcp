using ApprovalBridge.Broker.Core;
using ApprovalBridge.Broker.Events;
using ApprovalBridge.Broker.Model;
using ApprovalBridge.Broker.Registry;
using ApprovalBridge.Broker.Store;
using Json.Schema;
using Sinapsi.Nats.EventPlane;

namespace ApprovalBridge.Broker.Tests;

/// <summary>A settable clock so expiry / one-shot windows are exercised deterministically.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Records every emitted bridge fact so tests can assert subjects, verdicts, envelope fields,
/// and correlation-id joining.</summary>
internal sealed class RecordingEmitter : IApprovalEventEmitter
{
    public List<ApprovalFact> Facts { get; } = [];
    public ValueTask EmitAsync(ApprovalFact fact, CancellationToken ct = default)
    {
        Facts.Add(fact);
        return ValueTask.CompletedTask;
    }
    public IEnumerable<string> Verdicts => Facts.Select(f => f.Verdict);
}

/// <summary>Records dead-letters routed through the C2 <see cref="DeadLetterRouter"/>.</summary>
internal sealed class RecordingDeadLetterSink : IDeadLetterSink
{
    public List<(DeadLetterOutcome Outcome, string ChangeRef)> Writes { get; } = [];
    public ValueTask WriteAsync(DeadLetterOutcome outcome, string changeRef, CancellationToken ct = default)
    {
        Writes.Add((outcome, changeRef));
        return ValueTask.CompletedTask;
    }
}

/// <summary>A dispatcher that records commands and returns a configurable ack — to prove the act-command
/// shape (kind/target/correlation, no secret) and that dispatch happens exactly once. For the
/// deny-by-default proof tests use the real <see cref="NullActCommandDispatcher"/> instead.</summary>
internal sealed class RecordingDispatcher(ActCommandAck ack) : IActCommandDispatcher
{
    public List<ActCommand> Commands { get; } = [];
    public ValueTask<ActCommandAck> DispatchAsync(ActCommand command, CancellationToken ct = default)
    {
        Commands.Add(command);
        return ValueTask.FromResult(ack);
    }
}

/// <summary>Builds a broker over in-memory deps for the security-invariant tests.</summary>
internal static class BrokerFixture
{
    public const string DemoActionId = "garmin.oauth.exchange";

    // The demo action's param_schema (docs/66 §6 / E1.1 garmin.oauth.exchange.yaml), as JSON Schema.
    private const string DemoParamSchema = """
        {
          "type": "object",
          "required": ["auth_code"],
          "additionalProperties": false,
          "properties": { "auth_code": { "type": "string", "minLength": 8, "maxLength": 512 } }
        }
        """;

    public static ActionSpec DemoSpec(int expirySeconds = 300) => new(
        ActionId: DemoActionId,
        Title: "Garmin OAuth code→token exchange",
        Description: "Exchange a Garmin OAuth authorization code for a token, server-side.",
        TargetHost: "ct199-garmin",
        TargetIdentity: "garmin-connector",
        Executor: "garmin-oauth-exchange",
        ParamSchema: JsonSchema.FromText(DemoParamSchema),
        RiskTier: "yellow",
        ExpirySeconds: expirySeconds,
        RateLimit: new RateLimit(PerAgentPerHour: 3, PerActionPerHour: 10),
        OneShot: true);

    public sealed record Harness(
        BridgeBroker Broker,
        InMemoryApprovalStore Store,
        RecordingEmitter Emitter,
        IActCommandDispatcher Dispatcher,
        TestClock Clock);

    /// <summary>Build a broker. Defaults to the real deny-by-default <see cref="NullActCommandDispatcher"/>.</summary>
    public static Harness Build(
        IActCommandDispatcher? dispatcher = null,
        int expirySeconds = 300,
        IEnumerable<ActionSpec>? specs = null,
        DateTimeOffset? now = null)
    {
        var clock = new TestClock(now ?? DateTimeOffset.Parse("2026-07-15T09:00:00Z"));
        var store = new InMemoryApprovalStore();
        var emitter = new RecordingEmitter();
        var disp = dispatcher ?? new NullActCommandDispatcher();
        var registry = new InMemoryActionRegistry(specs ?? [DemoSpec(expirySeconds)]);
        var broker = new BridgeBroker(registry, store, emitter, disp, new InMemoryRateLimiter(), clock, new CryptoNonceSource());
        return new Harness(broker, store, emitter, disp, clock);
    }

    public static string ValidParams => """{ "auth_code": "abcd1234efgh" }""";
}
