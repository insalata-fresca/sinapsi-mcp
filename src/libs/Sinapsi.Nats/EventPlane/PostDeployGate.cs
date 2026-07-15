namespace Sinapsi.Nats.EventPlane;

/// <summary>One synthetic canary probe against the freshly-deployed surface — the REAL request path
/// (e.g. an MCP <c>tools/call</c> through the agentgateway), not a bare port-up check. A schema/session
/// break makes <see cref="ProbeAsync"/> return a failing <see cref="SyntheticProbeResult"/>, which is
/// exactly the class of break a port-up check would miss (home-server <c>docs/64 §3</c>). The live
/// deploy-controller implements this with an agentgateway client; the tests inject a canned result.</summary>
public interface ISyntheticProbe
{
    /// <summary>Run the canary once and report whether it passed.</summary>
    ValueTask<SyntheticProbeResult> ProbeAsync(CancellationToken ct = default);
}

/// <summary>How the bake window is sampled: how many re-reads to take and how long to wait between
/// them. The product (<see cref="Samples"/>-1) × <see cref="Interval"/> is the wall-clock the intended
/// state must hold; it MUST span at least the <see cref="BakeWindow.Duration"/> or the verifier reports
/// <see cref="EffectStatus.Verifying"/> (never success). Kept separate from <see cref="BakeWindow"/> so
/// the SCHEDULE (how often to look) is independent of the CONTRACT (how long / how many must hold).</summary>
/// <param name="Samples">Number of health re-reads to take (≥ the window's MinSamples).</param>
/// <param name="Interval">Wait between successive re-reads.</param>
public sealed record BakeSchedule(int Samples, TimeSpan Interval)
{
    /// <summary>A schedule that satisfies <paramref name="window"/> exactly: MinSamples reads spread so
    /// the last is <c>Duration</c> after the first.</summary>
    public static BakeSchedule ForWindow(BakeWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var gaps = Math.Max(1, window.MinSamples - 1);
        return new BakeSchedule(window.MinSamples, window.Duration / gaps);
    }
}

/// <summary>Where the post-deploy gate ended up. Only <see cref="Succeeded"/> declares the deploy done;
/// every other status either reverted the cutover or demands the human floor — never a silent pass.</summary>
public enum PostDeployStatus
{
    /// <summary>Health re-reads baked AND every canary passed — this, and only this, is "succeeded".</summary>
    Succeeded,
    /// <summary>Verify/canary failed and the auto-rollback ran to completion (the prior known-good image
    /// is back). The cutover is reverted; the service is running the previous revision.</summary>
    RolledBack,
    /// <summary>Verify/canary failed and the rollback could NOT proceed (compensator unreachable, or
    /// downstream already acted). The bad revision may still be live — escalate to the human floor.</summary>
    RollbackBlockedEscalate,
    /// <summary>Verify/canary failed, the rollback branch RAN, but the compensator itself faulted — the
    /// revert did not complete. Escalate; never treat as reverted.</summary>
    RollbackFailedEscalate,
}

/// <summary>The full post-deploy verdict: the verification, the synthetic gate, and (if the deploy did
/// not bake clean) the rollback outcome, folded into one <see cref="PostDeployStatus"/>.</summary>
/// <param name="Status">The folded outcome.</param>
/// <param name="Verification">The bake/verify result over the health re-reads.</param>
/// <param name="Gate">The synthetic-canary admission decision.</param>
/// <param name="Rollback">The rollback outcome — null iff the deploy succeeded (no rollback attempted).</param>
/// <param name="Reason">Human-readable audit line.</param>
public sealed record PostDeployOutcome(
    PostDeployStatus Status,
    VerificationOutcome Verification,
    SyntheticGateDecision Gate,
    RollbackOutcome? Rollback,
    string Reason)
{
    /// <summary>True only when the deploy baked clean and no rollback was needed.</summary>
    public bool Succeeded => Status == PostDeployStatus.Succeeded;

    /// <summary>True when the auto-rollback reverted the cutover to the prior known-good image.</summary>
    public bool RolledBack => Status == PostDeployStatus.RolledBack;

    /// <summary>True when the human floor must be told (rollback blocked or the compensator faulted).</summary>
    public bool EscalationRequired =>
        Status is PostDeployStatus.RollbackBlockedEscalate or PostDeployStatus.RollbackFailedEscalate;
}

/// <summary>
/// The reusable post-deploy VERIFY + BAKE + AUTO-ROLLBACK step (home-server <c>docs/64 §3</c>): the
/// missing health-gate between "restart" and "declared done". It COMPOSES the C3 primitives — it does
/// not reinvent them:
/// <list type="bullet">
///   <item><see cref="PostActionVerifier"/> — turns the health re-reads into an
///     <see cref="EffectStatus"/> under the mandatory <see cref="BakeWindow"/> (acknowledged ≠ effective).</item>
///   <item><see cref="SyntheticGate"/> — a representative canary through the REAL request path must pass
///     (a port-up check would not catch a schema/session break).</item>
///   <item><see cref="ConditionalRollback"/> — on failure, revert to the prior known-good image, but only
///     when that is safe; otherwise escalate.</item>
///   <item><see cref="IdempotentExecutor"/> — the whole gate runs at most once per end-to-end deploy
///     change-id, so a re-delivered release event replays the recorded outcome instead of
///     re-verifying / double-rolling-back.</item>
/// </list>
///
/// <para>Timing is injected (<c>delay</c> + the probe-owned sample timestamps) so every branch —
/// baked-clean, failed-health-rollback, failed-canary-rollback, regressed, rollback-blocked, and
/// compensator-fault — is deterministically unit-testable with a no-op delay.</para>
/// </summary>
public static class PostDeployGate
{
    /// <summary>Run the gate once for a deploy identified by <paramref name="changeId"/>, guarded by the
    /// idempotency ledger so a re-driven release replays the recorded outcome. See
    /// <see cref="RunAsync(IEffectProbe,IReadOnlyList{ISyntheticProbe},ConditionalRollback,BakeWindow,BakeSchedule,Func{TimeSpan,CancellationToken,Task}?,CancellationToken)"/>
    /// for the parameters.</summary>
    public static async ValueTask<PostDeployOutcome> RunOnceAsync(
        IIdempotencyStore store,
        string changeId,
        IEffectProbe health,
        IReadOnlyList<ISyntheticProbe> canaries,
        ConditionalRollback rollback,
        BakeWindow window,
        BakeSchedule schedule,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        // The recorded result is a compact status token; the fresh run returns the rich outcome. On a
        // replay we only know the prior STATUS (the ledger stores a string), which is all a re-drive
        // needs — the effect (verify/rollback) has already happened and must not repeat.
        PostDeployOutcome? fresh = null;
        var idem = await IdempotentExecutor.RunOnceAsync(store, changeId, async token =>
        {
            fresh = await RunAsync(health, canaries, rollback, window, schedule, delay, token).ConfigureAwait(false);
            return fresh.Status.ToString();
        }, ct).ConfigureAwait(false);

        if (fresh is not null)
            return fresh; // this call actually executed the gate

        // Replayed: reconstruct a minimal outcome carrying the recorded status (no re-verify, no re-rollback).
        var replayedStatus = Enum.TryParse<PostDeployStatus>(idem.ResultJson, out var s) ? s : PostDeployStatus.RollbackBlockedEscalate;
        var note = $"replayed prior post-deploy outcome for change '{changeId}' (idempotent re-drive — gate not re-run)";
        return new PostDeployOutcome(
            replayedStatus,
            new VerificationOutcome(replayedStatus == PostDeployStatus.Succeeded ? EffectStatus.Effective : EffectStatus.Unverified, note),
            new SyntheticGateDecision(
                replayedStatus == PostDeployStatus.Succeeded ? TrafficPhase.RealTrafficAdmitted : TrafficPhase.SyntheticOnly,
                replayedStatus == PostDeployStatus.Succeeded,
                Array.Empty<string>()),
            Rollback: null,
            Reason: note);
    }

    /// <summary>Drive the gate: sample health across the bake window, run the canaries, and — if the
    /// deploy did not verify clean — attempt the conditional rollback.</summary>
    /// <param name="health">Re-reads the deployed service's health/intended state (the event-based SLI).</param>
    /// <param name="canaries">Representative synthetic probes through the real request path.</param>
    /// <param name="rollback">Pre-wired revert-to-prior-known-good, gated by reachability + downstream.</param>
    /// <param name="window">The mandatory bake contract (how long / how many samples).</param>
    /// <param name="schedule">The sampling schedule (how many re-reads, how far apart).</param>
    /// <param name="delay">Injected wait between samples (default <see cref="Task.Delay(TimeSpan,CancellationToken)"/>);
    /// tests pass a no-op.</param>
    public static async ValueTask<PostDeployOutcome> RunAsync(
        IEffectProbe health,
        IReadOnlyList<ISyntheticProbe> canaries,
        ConditionalRollback rollback,
        BakeWindow window,
        BakeSchedule schedule,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(canaries);
        ArgumentNullException.ThrowIfNull(rollback);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(schedule);
        delay ??= static (d, token) => Task.Delay(d, token);

        // 1. Collect health re-reads AND canary results across the bake window. Both are sampled every
        //    tick so the canary must hold CONTINUOUSLY, not just once — a flaky schema/session break is
        //    caught by any failing tick.
        var samples = new List<EffectSample>(Math.Max(1, schedule.Samples));
        var canaryResults = new List<SyntheticProbeResult>();
        for (var i = 0; i < Math.Max(1, schedule.Samples); i++)
        {
            if (i > 0)
                await delay(schedule.Interval, ct).ConfigureAwait(false);

            samples.Add(await health.ReadAsync("post-deploy", ct).ConfigureAwait(false));

            foreach (var canary in canaries)
            {
                var r = await canary.ProbeAsync(ct).ConfigureAwait(false);
                // Tag the tick so a mid-bake canary failure is legible in the audit.
                canaryResults.Add(r with { Name = $"{r.Name}@tick{i}" });
            }
        }

        // 2. Verify the bake + gate the synthetic canaries.
        var verification = PostActionVerifier.Evaluate(acknowledged: true, window, samples);
        var gate = SyntheticGate.Evaluate(canaryResults, bakeWindowSatisfied: verification.Succeeded);

        if (verification.Succeeded && gate.AdmitRealTraffic)
            return new PostDeployOutcome(
                PostDeployStatus.Succeeded, verification, gate, Rollback: null,
                Reason: $"deploy verified: {verification.Reason}; canaries admitted real traffic");

        // 3. Not clean → auto-rollback to the prior known-good image (conditionally).
        var blockers = string.Join("; ", gate.Blockers);
        var failReason = verification.Succeeded
            ? $"health baked but canary gate withheld traffic: {blockers}"
            : $"health did not bake: {verification.Reason}" + (gate.AdmitRealTraffic ? "" : $"; {blockers}");

        var rb = await rollback.TryRollbackAsync(ct).ConfigureAwait(false);
        var status = rb switch
        {
            { Compensated: true } => PostDeployStatus.RolledBack,
            { EscalationRequired: true, Decision.CanProceed: false } => PostDeployStatus.RollbackBlockedEscalate,
            _ => PostDeployStatus.RollbackFailedEscalate,
        };

        var reason = status switch
        {
            PostDeployStatus.RolledBack => $"AUTO-ROLLBACK fired ({failReason}); reverted to prior known-good: {rb.Decision.Reason}",
            PostDeployStatus.RollbackBlockedEscalate => $"ESCALATE — rollback BLOCKED ({failReason}); {rb.Decision.Reason}",
            _ => $"ESCALATE — rollback FAILED ({failReason}); {rb.Detail ?? rb.Decision.Reason}",
        };

        return new PostDeployOutcome(status, verification, gate, rb, reason);
    }
}
