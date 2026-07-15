using Sinapsi.Nats.EventPlane;
using Xunit;

namespace Sinapsi.Nats.Tests.EventPlane;

/// <summary>
/// C3 — the reusable POST-DEPLOY VERIFY + BAKE + AUTO-ROLLBACK gate (home-server task #56, the missing
/// health-gate that the CT121-mcp-gateway two-plane incident lacked). Proves the composition of
/// PostActionVerifier + SyntheticGate + ConditionalRollback + IdempotentExecutor takes the right branch
/// for every deploy outcome — and, critically, that a FAILED health/canary actually FIRES the rollback
/// (the failing-fixture the DoD requires), not just the happy path.
/// </summary>
public sealed class PostDeployGateTests
{
    // A 10s / 3-sample bake, sampled at 0s,5s,10s so a clean run spans exactly the window.
    private static readonly BakeWindow Window = BakeWindow.Require(TimeSpan.FromSeconds(10), minSamples: 3);
    private static readonly BakeSchedule Schedule = new(Samples: 3, Interval: TimeSpan.FromSeconds(5));

    // No real sleeping in tests.
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = static (_, _) => Task.CompletedTask;

    /// <summary>Health probe that replays a fixed match/no-match pattern with monotonically advancing
    /// timestamps (5s apart), so the bake span is deterministic regardless of the no-op delay.</summary>
    private sealed class FakeHealthProbe : IEffectProbe
    {
        private readonly bool[] _pattern;
        private int _i;
        public FakeHealthProbe(params bool[] pattern) => _pattern = pattern;
        public int Calls { get; private set; }
        public ValueTask<EffectSample> ReadAsync(string changeId, CancellationToken ct = default)
        {
            var matches = _pattern[Math.Min(_i, _pattern.Length - 1)];
            var at = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(5 * _i);
            _i++; Calls++;
            return ValueTask.FromResult(new EffectSample(at, matches, $"health call {_i}"));
        }
    }

    /// <summary>A canary through the real path whose pass/fail is fixed by the test.</summary>
    private sealed class FakeCanary : ISyntheticProbe
    {
        private readonly bool _passes;
        private readonly string _name;
        public FakeCanary(bool passes, string name = "mcp-tools-list") { _passes = passes; _name = name; }
        public int Calls { get; private set; }
        public ValueTask<SyntheticProbeResult> ProbeAsync(CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(new SyntheticProbeResult(_name, _passes, _passes ? null : "schema/session break through agentgateway"));
        }
    }

    private static ConditionalRollback Rollback(RecordingCompensator comp, bool reachable = true, bool downstreamActed = false)
        => new(new StubReachabilityProbe(reachable), new StubDownstreamActivityProbe(downstreamActed), comp);

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Succeeds_and_does_not_roll_back_when_health_bakes_and_canary_passes()
    {
        var comp = new RecordingCompensator(throws: false);
        var outcome = await PostDeployGate.RunAsync(
            new FakeHealthProbe(true, true, true), new[] { new FakeCanary(true) }, Rollback(comp),
            Window, Schedule, NoDelay);

        Assert.Equal(PostDeployStatus.Succeeded, outcome.Status);
        Assert.True(outcome.Succeeded);
        Assert.False(comp.Invoked);                 // NO rollback on a clean deploy
        Assert.Equal(EffectStatus.Effective, outcome.Verification.Status);
        Assert.True(outcome.Gate.AdmitRealTraffic);
    }

    // ── The failing-fixture: a bad deploy MUST fire the rollback ─────────────────

    [Fact]
    public async Task Rolls_back_when_health_never_becomes_effective()
    {
        var comp = new RecordingCompensator(throws: false);
        var outcome = await PostDeployGate.RunAsync(
            new FakeHealthProbe(false, false, false), new[] { new FakeCanary(true) }, Rollback(comp),
            Window, Schedule, NoDelay);

        Assert.Equal(PostDeployStatus.RolledBack, outcome.Status);
        Assert.True(comp.Invoked);                  // <-- rollback branch actually fired
        Assert.True(outcome.RolledBack);
        Assert.NotNull(outcome.Rollback);
        Assert.True(outcome.Rollback!.Compensated);
        Assert.Contains("AUTO-ROLLBACK", outcome.Reason);
    }

    [Fact]
    public async Task Rolls_back_when_canary_fails_even_though_the_port_is_up()
    {
        // Health re-reads all pass (the container is UP) but the representative call through the
        // agentgateway fails — exactly the schema/session break a port-up check would have missed.
        var comp = new RecordingCompensator(throws: false);
        var outcome = await PostDeployGate.RunAsync(
            new FakeHealthProbe(true, true, true), new[] { new FakeCanary(passes: false) }, Rollback(comp),
            Window, Schedule, NoDelay);

        Assert.Equal(PostDeployStatus.RolledBack, outcome.Status);
        Assert.True(comp.Invoked);
        Assert.Equal(EffectStatus.Effective, outcome.Verification.Status);   // health baked...
        Assert.False(outcome.Gate.AdmitRealTraffic);                          // ...but the gate withheld
        Assert.Contains("canary", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rolls_back_when_effect_regresses_mid_bake()
    {
        var comp = new RecordingCompensator(throws: false);
        var outcome = await PostDeployGate.RunAsync(
            new FakeHealthProbe(true, true, false), new[] { new FakeCanary(true) }, Rollback(comp),
            Window, Schedule, NoDelay);

        Assert.Equal(PostDeployStatus.RolledBack, outcome.Status);
        Assert.Equal(EffectStatus.Regressed, outcome.Verification.Status);
        Assert.True(comp.Invoked);
    }

    // ── Escalation branches (rollback is conditional, not free) ──────────────────

    [Fact]
    public async Task Escalates_without_compensating_when_downstream_already_acted()
    {
        var comp = new RecordingCompensator(throws: false);
        var outcome = await PostDeployGate.RunAsync(
            new FakeHealthProbe(false, false, false), new[] { new FakeCanary(true) },
            Rollback(comp, reachable: true, downstreamActed: true),
            Window, Schedule, NoDelay);

        Assert.Equal(PostDeployStatus.RollbackBlockedEscalate, outcome.Status);
        Assert.True(outcome.EscalationRequired);
        Assert.False(comp.Invoked);                 // must NOT undo when downstream has consumed it
        Assert.Contains("ESCALATE", outcome.Reason);
    }

    [Fact]
    public async Task Escalates_when_compensator_faults()
    {
        var comp = new RecordingCompensator(throws: true);
        var outcome = await PostDeployGate.RunAsync(
            new FakeHealthProbe(false, false, false), new[] { new FakeCanary(true) }, Rollback(comp),
            Window, Schedule, NoDelay);

        Assert.Equal(PostDeployStatus.RollbackFailedEscalate, outcome.Status);
        Assert.True(outcome.EscalationRequired);
        Assert.True(comp.Invoked);                  // the branch ran; the compensator itself threw
        Assert.False(outcome.Rollback!.Compensated);
    }

    // ── Idempotent re-drive: a re-delivered release event must NOT re-run the gate ──

    [Fact]
    public async Task Idempotent_redrive_replays_and_does_not_roll_back_twice()
    {
        var store = new InMemoryIdempotencyStore();
        var comp = new RecordingCompensator(throws: false);
        const string changeId = "chg_deadbeef";

        var first = await PostDeployGate.RunOnceAsync(
            store, changeId, new FakeHealthProbe(false, false, false), new[] { new FakeCanary(true) },
            Rollback(comp), Window, Schedule, NoDelay);

        // Second delivery of the same release: a fresh probe/canary/compensator that would flip the
        // result if it ran — it must NOT run.
        var secondComp = new RecordingCompensator(throws: false);
        var second = await PostDeployGate.RunOnceAsync(
            store, changeId, new FakeHealthProbe(true, true, true), new[] { new FakeCanary(true) },
            Rollback(secondComp), Window, Schedule, NoDelay);

        Assert.Equal(PostDeployStatus.RolledBack, first.Status);
        Assert.True(comp.Invoked);
        Assert.Equal(PostDeployStatus.RolledBack, second.Status);   // replayed the FIRST outcome
        Assert.False(secondComp.Invoked);                            // the gate did not re-run
        Assert.Contains("replayed", second.Reason);
    }

    [Fact]
    public void BakeSchedule_ForWindow_spans_the_window()
    {
        var s = BakeSchedule.ForWindow(BakeWindow.Require(TimeSpan.FromSeconds(30), minSamples: 4));
        Assert.Equal(4, s.Samples);
        Assert.Equal(TimeSpan.FromSeconds(10), s.Interval);   // (4-1) * 10s = 30s
    }
}
