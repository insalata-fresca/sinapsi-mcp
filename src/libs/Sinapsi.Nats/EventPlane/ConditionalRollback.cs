namespace Sinapsi.Nats.EventPlane;

/// <summary>Whether a rollback may proceed. Reversibility is CONDITIONAL, not free
/// (home-server <c>docs/64 §3</c>): a rollback is only safe when the compensator is reachable AND
/// downstream has not already acted on the change being reversed.</summary>
public enum RollbackDisposition
{
    /// <summary>Both preconditions hold — the compensator may run.</summary>
    Proceed,
    /// <summary>The compensator cannot be reached — rolling back would leave an unknown state.
    /// Do not attempt; escalate.</summary>
    BlockedCompensatorUnreachable,
    /// <summary>Downstream has already consumed/acted on the change — reversing now would create an
    /// inconsistency (the classic "you cannot un-send the email"). Do not attempt; escalate.</summary>
    BlockedDownstreamActed,
}

/// <summary>The evaluated rollback precondition decision.</summary>
/// <param name="Disposition">Whether rollback may proceed and, if not, why.</param>
/// <param name="Reason">Human-readable justification (audit).</param>
public sealed record RollbackDecision(RollbackDisposition Disposition, string Reason)
{
    /// <summary>True only when both preconditions hold.</summary>
    public bool CanProceed => Disposition == RollbackDisposition.Proceed;
}

/// <summary>The result of an attempted conditional rollback.</summary>
/// <param name="Decision">The precondition decision that gated the attempt.</param>
/// <param name="Compensated">True iff the compensator actually ran to completion.</param>
/// <param name="EscalationRequired">True when the rollback could not safely proceed OR the
/// compensator itself failed — the human floor must be told; the change is NOT quietly abandoned.</param>
/// <param name="Detail">Extra context (e.g. a compensator fault message).</param>
public sealed record RollbackOutcome(RollbackDecision Decision, bool Compensated, bool EscalationRequired, string? Detail = null);

/// <summary>Probes whether the compensator (the thing that would undo the change) is reachable RIGHT
/// NOW. An unreachable compensator makes rollback unsafe — you cannot verify the undo took effect.</summary>
public interface IReachabilityProbe
{
    ValueTask<bool> IsReachableAsync(CancellationToken ct = default);
}

/// <summary>Probes whether DOWNSTREAM has already acted on the change being reversed (consumed the
/// merged code, shipped the deploy, sent a notification). If so, reversibility is lost.</summary>
public interface IDownstreamActivityProbe
{
    ValueTask<bool> HasDownstreamActedAsync(CancellationToken ct = default);
}

/// <summary>The undo action itself. Kept separate from the probes so the drill harness can inject a
/// faulting compensator and prove the failure branch is handled.</summary>
public interface ICompensator
{
    ValueTask CompensateAsync(CancellationToken ct = default);
}

/// <summary>The PURE conditional-reversibility rule (home-server <c>docs/64 §3</c>): reversibility is
/// not free. A rollback may proceed only when the compensator is reachable AND downstream has not
/// acted. Separated from the async executor so the decision table is exhaustively unit-testable.</summary>
public static class RollbackGate
{
    /// <summary>Evaluate the two preconditions. A downstream that already acted blocks even a
    /// reachable compensator — irreversibility dominates.</summary>
    public static RollbackDecision Evaluate(bool compensatorReachable, bool downstreamActed)
    {
        if (downstreamActed)
            return new RollbackDecision(RollbackDisposition.BlockedDownstreamActed,
                "downstream has already acted on this change — reversing now would create an inconsistency; escalate instead (docs/64 §3)");
        if (!compensatorReachable)
            return new RollbackDecision(RollbackDisposition.BlockedCompensatorUnreachable,
                "the compensator is unreachable — a rollback whose effect cannot be verified is unsafe; escalate instead (docs/64 §3)");
        return new RollbackDecision(RollbackDisposition.Proceed, "compensator reachable and downstream has not acted");
    }
}

/// <summary>
/// Executes a rollback ONLY when <see cref="RollbackGate"/> permits it, and treats a compensator
/// fault as an escalation (never a silent success). This is the "reversibility is conditional, not
/// free" machinery — the act-path wires it with real probes + a real compensator; the
/// <see cref="RollbackDrillHarness"/> exercises it with injected faults ("an unexercised rollback is
/// an assumption", home-server <c>docs/64 §3</c>).
/// </summary>
public sealed class ConditionalRollback
{
    private readonly IReachabilityProbe _reachability;
    private readonly IDownstreamActivityProbe _downstream;
    private readonly ICompensator _compensator;

    public ConditionalRollback(IReachabilityProbe reachability, IDownstreamActivityProbe downstream, ICompensator compensator)
    {
        _reachability = reachability ?? throw new ArgumentNullException(nameof(reachability));
        _downstream = downstream ?? throw new ArgumentNullException(nameof(downstream));
        _compensator = compensator ?? throw new ArgumentNullException(nameof(compensator));
    }

    /// <summary>Probe the preconditions, and compensate only if both hold. Any block — or a fault in
    /// the compensator itself — yields <see cref="RollbackOutcome.EscalationRequired"/> = true.</summary>
    public async ValueTask<RollbackOutcome> TryRollbackAsync(CancellationToken ct = default)
    {
        // Probe downstream first: if it has acted, we must not even attempt a reach/compensate.
        var downstreamActed = await _downstream.HasDownstreamActedAsync(ct);
        var reachable = downstreamActed ? false : await _reachability.IsReachableAsync(ct);
        var decision = RollbackGate.Evaluate(reachable, downstreamActed);

        if (!decision.CanProceed)
            return new RollbackOutcome(decision, Compensated: false, EscalationRequired: true);

        try
        {
            await _compensator.CompensateAsync(ct);
            return new RollbackOutcome(decision, Compensated: true, EscalationRequired: false);
        }
        catch (Exception ex)
        {
            // The rollback branch RAN but the compensator failed — surface it, do not swallow it.
            return new RollbackOutcome(decision, Compensated: false, EscalationRequired: true,
                Detail: $"compensator threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
