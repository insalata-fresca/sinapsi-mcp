namespace Sinapsi.Nats.EventPlane;

/// <summary>One fault-injection scenario for a rollback drill. Every field is a lever the drill
/// pulls to force the rollback machinery down a specific branch — including the failure branches an
/// unexercised rollback would leave untested (home-server <c>docs/64 §3</c>: "a rollback path is
/// untrusted until exercised … rollback drills / fault injection").</summary>
/// <param name="Name">Scenario label (appears in the report).</param>
/// <param name="CompensatorReachable">Whether the injected reachability probe reports reachable.</param>
/// <param name="DownstreamActed">Whether the injected downstream probe reports it already acted.</param>
/// <param name="CompensatorThrows">Whether the injected compensator faults when invoked.</param>
public sealed record RollbackDrillScenario(string Name, bool CompensatorReachable, bool DownstreamActed, bool CompensatorThrows);

/// <summary>The forensic result of running one <see cref="RollbackDrillScenario"/>.</summary>
/// <param name="Name">The scenario label.</param>
/// <param name="Disposition">Which precondition branch the gate took.</param>
/// <param name="CompensatorInvoked">True iff the compensator's body actually executed — the proof
/// that the rollback branch was EXERCISED, not merely assumed.</param>
/// <param name="Compensated">True iff the compensator ran to completion without faulting.</param>
/// <param name="EscalationRequired">Whether the outcome demands the human floor.</param>
/// <param name="Detail">Extra context (fault message, etc.).</param>
public sealed record RollbackDrillResult(
    string Name,
    RollbackDisposition Disposition,
    bool CompensatorInvoked,
    bool Compensated,
    bool EscalationRequired,
    string? Detail);

// --- Fault-injecting fakes, public so the act-path's own drill suites can reuse them. ---

/// <summary>A reachability probe whose answer is fixed by the drill.</summary>
public sealed class StubReachabilityProbe : IReachabilityProbe
{
    private readonly bool _reachable;
    public StubReachabilityProbe(bool reachable) => _reachable = reachable;
    public ValueTask<bool> IsReachableAsync(CancellationToken ct = default) => ValueTask.FromResult(_reachable);
}

/// <summary>A downstream-activity probe whose answer is fixed by the drill.</summary>
public sealed class StubDownstreamActivityProbe : IDownstreamActivityProbe
{
    private readonly bool _acted;
    public StubDownstreamActivityProbe(bool acted) => _acted = acted;
    public ValueTask<bool> HasDownstreamActedAsync(CancellationToken ct = default) => ValueTask.FromResult(_acted);
}

/// <summary>A compensator that records whether it was invoked and can be told to fault — the core
/// fault-injection lever. <see cref="Invoked"/> is what proves the rollback branch actually ran.</summary>
public sealed class RecordingCompensator : ICompensator
{
    private readonly bool _throws;
    public RecordingCompensator(bool throws) => _throws = throws;

    /// <summary>True once <see cref="CompensateAsync"/> has been entered.</summary>
    public bool Invoked { get; private set; }

    public ValueTask CompensateAsync(CancellationToken ct = default)
    {
        Invoked = true;
        if (_throws)
            throw new InvalidOperationException("injected compensator fault");
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Drives <see cref="ConditionalRollback"/> through injected-fault scenarios and reports which branch
/// each took and whether the compensator body actually ran. The canon's point is that a rollback path
/// you have never fired is an ASSUMPTION, not a capability; this harness fires it — including the
/// downstream-acted, unreachable, and compensator-fault branches — so those are proven, not assumed.
/// </summary>
public static class RollbackDrillHarness
{
    /// <summary>Run one scenario against a freshly-built <see cref="ConditionalRollback"/> wired with
    /// the injected fakes, and return a forensic <see cref="RollbackDrillResult"/>.</summary>
    public static async ValueTask<RollbackDrillResult> RunAsync(RollbackDrillScenario scenario, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var compensator = new RecordingCompensator(scenario.CompensatorThrows);
        var rollback = new ConditionalRollback(
            new StubReachabilityProbe(scenario.CompensatorReachable),
            new StubDownstreamActivityProbe(scenario.DownstreamActed),
            compensator);

        var outcome = await rollback.TryRollbackAsync(ct);

        return new RollbackDrillResult(
            scenario.Name,
            outcome.Decision.Disposition,
            CompensatorInvoked: compensator.Invoked,
            Compensated: outcome.Compensated,
            EscalationRequired: outcome.EscalationRequired,
            Detail: outcome.Detail);
    }

    /// <summary>The canonical drill matrix: the four branches every act-path rollback MUST have
    /// exercised before it is trusted. Returned as data so a suite can assert on each.</summary>
    public static IReadOnlyList<RollbackDrillScenario> StandardMatrix { get; } = new[]
    {
        new RollbackDrillScenario("happy-path-compensates", CompensatorReachable: true, DownstreamActed: false, CompensatorThrows: false),
        new RollbackDrillScenario("blocked-downstream-acted", CompensatorReachable: true, DownstreamActed: true, CompensatorThrows: false),
        new RollbackDrillScenario("blocked-unreachable", CompensatorReachable: false, DownstreamActed: false, CompensatorThrows: false),
        new RollbackDrillScenario("compensator-fault-escalates", CompensatorReachable: true, DownstreamActed: false, CompensatorThrows: true),
    };

    /// <summary>Run the whole <see cref="StandardMatrix"/>.</summary>
    public static async ValueTask<IReadOnlyList<RollbackDrillResult>> RunStandardMatrixAsync(CancellationToken ct = default)
    {
        var results = new List<RollbackDrillResult>(StandardMatrix.Count);
        foreach (var s in StandardMatrix)
            results.Add(await RunAsync(s, ct));
        return results;
    }
}
