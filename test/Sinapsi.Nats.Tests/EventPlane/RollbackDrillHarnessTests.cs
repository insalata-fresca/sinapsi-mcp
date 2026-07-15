using Sinapsi.Nats.EventPlane;
using Xunit;

namespace Sinapsi.Nats.Tests.EventPlane;

/// <summary>C3 item 3b — ROLLBACK-DRILL / fault-injection harness. Proves the rollback branch is
/// actually EXERCISED (an unexercised rollback is an assumption) including the compensator-fault
/// branch (docs/64 §3).</summary>
public sealed class RollbackDrillHarnessTests
{
    [Fact]
    public async Task HappyPath_InvokesCompensator_AndSucceeds()
    {
        var r = await RollbackDrillHarness.RunAsync(
            new RollbackDrillScenario("happy", CompensatorReachable: true, DownstreamActed: false, CompensatorThrows: false));

        Assert.True(r.CompensatorInvoked);   // the branch actually ran — proven, not assumed
        Assert.True(r.Compensated);
        Assert.False(r.EscalationRequired);
    }

    [Fact]
    public async Task CompensatorFault_IsExercised_AndSurfacedAsEscalation()
    {
        // The whole point of the drill: fire the failure branch. The compensator body runs, throws,
        // and the machinery surfaces it as an escalation rather than a silent success.
        var r = await RollbackDrillHarness.RunAsync(
            new RollbackDrillScenario("fault", CompensatorReachable: true, DownstreamActed: false, CompensatorThrows: true));

        Assert.True(r.CompensatorInvoked);   // the rollback path was exercised
        Assert.False(r.Compensated);         // but it did not complete
        Assert.True(r.EscalationRequired);
        Assert.Contains("compensator threw", r.Detail);
    }

    [Fact]
    public async Task StandardMatrix_ExercisesEveryBranch()
    {
        var results = await RollbackDrillHarness.RunStandardMatrixAsync();
        var byName = results.ToDictionary(r => r.Name);

        // Every one of the four canonical branches is present and lands where the canon requires.
        Assert.Equal(4, results.Count);

        Assert.Equal(RollbackDisposition.Proceed, byName["happy-path-compensates"].Disposition);
        Assert.True(byName["happy-path-compensates"].Compensated);

        Assert.Equal(RollbackDisposition.BlockedDownstreamActed, byName["blocked-downstream-acted"].Disposition);
        Assert.False(byName["blocked-downstream-acted"].CompensatorInvoked);
        Assert.True(byName["blocked-downstream-acted"].EscalationRequired);

        Assert.Equal(RollbackDisposition.BlockedCompensatorUnreachable, byName["blocked-unreachable"].Disposition);
        Assert.False(byName["blocked-unreachable"].CompensatorInvoked);

        // The fault branch is the one an unexercised rollback would never have proven.
        Assert.True(byName["compensator-fault-escalates"].CompensatorInvoked);
        Assert.True(byName["compensator-fault-escalates"].EscalationRequired);
    }
}
