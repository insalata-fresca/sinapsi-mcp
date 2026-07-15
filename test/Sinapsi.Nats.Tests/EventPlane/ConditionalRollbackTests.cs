using Sinapsi.Nats.EventPlane;
using Xunit;

namespace Sinapsi.Nats.Tests.EventPlane;

/// <summary>C3 item 3a — ROLLBACK as CONDITIONAL, not free. Proves the gate blocks when the
/// compensator is unreachable or downstream has already acted (docs/64 §3).</summary>
public sealed class ConditionalRollbackTests
{
    [Fact]
    public void Gate_Proceeds_OnlyWhenReachableAndDownstreamIdle()
    {
        var d = RollbackGate.Evaluate(compensatorReachable: true, downstreamActed: false);
        Assert.Equal(RollbackDisposition.Proceed, d.Disposition);
        Assert.True(d.CanProceed);
    }

    [Fact]
    public void Gate_Blocks_WhenDownstreamActed_EvenIfReachable()
    {
        // Irreversibility dominates: a reachable compensator does not make an already-consumed change safe to undo.
        var d = RollbackGate.Evaluate(compensatorReachable: true, downstreamActed: true);
        Assert.Equal(RollbackDisposition.BlockedDownstreamActed, d.Disposition);
        Assert.False(d.CanProceed);
    }

    [Fact]
    public void Gate_Blocks_WhenCompensatorUnreachable()
    {
        var d = RollbackGate.Evaluate(compensatorReachable: false, downstreamActed: false);
        Assert.Equal(RollbackDisposition.BlockedCompensatorUnreachable, d.Disposition);
        Assert.False(d.CanProceed);
    }

    [Fact]
    public async Task Execute_Compensates_OnProceed()
    {
        var comp = new RecordingCompensator(throws: false);
        var rollback = new ConditionalRollback(
            new StubReachabilityProbe(true), new StubDownstreamActivityProbe(false), comp);

        var outcome = await rollback.TryRollbackAsync();

        Assert.True(outcome.Compensated);
        Assert.False(outcome.EscalationRequired);
        Assert.True(comp.Invoked);
    }

    [Fact]
    public async Task Execute_DoesNotCompensate_AndEscalates_WhenDownstreamActed()
    {
        var comp = new RecordingCompensator(throws: false);
        var rollback = new ConditionalRollback(
            new StubReachabilityProbe(true), new StubDownstreamActivityProbe(true), comp);

        var outcome = await rollback.TryRollbackAsync();

        Assert.False(outcome.Compensated);
        Assert.True(outcome.EscalationRequired);
        Assert.False(comp.Invoked);  // the compensator must NOT run when downstream already acted
        Assert.Equal(RollbackDisposition.BlockedDownstreamActed, outcome.Decision.Disposition);
    }

    [Fact]
    public async Task Execute_DoesNotCompensate_AndEscalates_WhenUnreachable()
    {
        var comp = new RecordingCompensator(throws: false);
        var rollback = new ConditionalRollback(
            new StubReachabilityProbe(false), new StubDownstreamActivityProbe(false), comp);

        var outcome = await rollback.TryRollbackAsync();

        Assert.False(outcome.Compensated);
        Assert.True(outcome.EscalationRequired);
        Assert.False(comp.Invoked);
    }
}
