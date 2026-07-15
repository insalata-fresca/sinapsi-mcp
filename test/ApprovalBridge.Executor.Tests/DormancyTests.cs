using ApprovalBridge.Executor.Dispatch;
using ApprovalBridge.Executor.Garmin;
using ApprovalBridge.Executor.Sdk;
using Sinapsi.Nats.EventPlane;
using Xunit;

namespace ApprovalBridge.Executor.Tests;

/// <summary>
/// DORMANCY PROOF (home-server <c>docs/66 §10</c>): the default posture is the C2
/// <see cref="NullActCommandDispatcher"/> (deny-by-default). The real <see cref="ExecutorDispatcher"/> is
/// selected ONLY when <c>live: true</c> is passed explicitly — the broker gates that behind an env flag that
/// defaults off, and moving to live-acting is a trust-boundary flip out of scope for E1.4.
/// </summary>
public sealed class DormancyTests
{
    private static readonly IActionExecutor[] Handlers =
        [new GarminOAuthExchangeExecutor(new NotProvisionedGarminEndpoint(), new NotProvisionedGarminTokenStore())];

    [Fact]
    public void SelectDispatcher_DefaultsToNull_WhenNotLive()
    {
        var dispatcher = ExecutorWiring.SelectDispatcher(
            live: false, actionsDir: Fixtures.FixturesDir, secretsRootDir: "/tmp/unused", handlers: Handlers);
        Assert.IsType<NullActCommandDispatcher>(dispatcher);
    }

    [Fact]
    public void SelectDispatcher_BuildsExecutor_OnlyWhenLive()
    {
        var dispatcher = ExecutorWiring.SelectDispatcher(
            live: true, actionsDir: Fixtures.FixturesDir, secretsRootDir: "/tmp/unused", handlers: Handlers);
        Assert.IsType<ExecutorDispatcher>(dispatcher);
    }

    [Fact]
    public async Task NullDefault_RejectsEveryApprovalBridgeExecute_NothingActs()
    {
        var dispatcher = ExecutorWiring.SelectDispatcher(
            live: false, actionsDir: Fixtures.FixturesDir, secretsRootDir: "/tmp/unused", handlers: Handlers);
        var ack = await dispatcher.DispatchAsync(Fixtures.ExecuteCommand(Fixtures.DemoActionId, Fixtures.ValidParams));
        Assert.False(ack.Accepted);
        Assert.Equal(NullActCommandDispatcher.RejectReason, ack.Reason);
        Assert.Null(ack.ResultJson);
    }

    [Fact]
    public async Task EvenLive_WithoutProvisionedGarminIntegration_ExecutesNothing()
    {
        // Flipping the flag without provisioning the real Garmin integration still acts on nothing: the
        // NotProvisioned endpoint refuses (deny-by-default one layer deeper than the broker seam).
        var live = ExecutorWiring.SelectDispatcher(
            live: true, actionsDir: Fixtures.FixturesDir, secretsRootDir: "/tmp/unused", handlers: Handlers);
        var ack = await live.DispatchAsync(Fixtures.ExecuteCommand(Fixtures.DemoActionId, Fixtures.ValidParams));
        Assert.False(ack.Accepted);
        Assert.Contains("not provisioned", ack.Reason);
    }
}
