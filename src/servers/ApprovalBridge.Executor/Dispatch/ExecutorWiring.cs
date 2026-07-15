using ApprovalBridge.Executor.PathD;
using ApprovalBridge.Executor.Registry;
using ApprovalBridge.Executor.Sdk;
using Sinapsi.Nats.EventPlane;

namespace ApprovalBridge.Executor.Dispatch;

/// <summary>
/// The single seam that decides whether the broker acts through the real executor or stays dormant. It
/// encodes the E1.4 default posture (home-server <c>docs/66 §10</c>): <b>the default is
/// <see cref="NullActCommandDispatcher"/></b> (deny-by-default / dormant). The real
/// <see cref="ExecutorDispatcher"/> is selected ONLY when the caller explicitly passes <c>live: true</c> —
/// which the broker gates behind an env flag that defaults off. Moving from dormant to live-acting is a
/// trust-boundary flip (an always-escalate step) and is out of scope for E1.4.
/// </summary>
public static class ExecutorWiring
{
    /// <summary>
    /// Select the dispatcher the broker binds to <c>IActCommandDispatcher</c>. When
    /// <paramref name="live"/> is false (the default posture) returns a <see cref="NullActCommandDispatcher"/>
    /// and NOTHING is built. When true, builds a real <see cref="ExecutorDispatcher"/> over the allowlist at
    /// <paramref name="actionsDir"/>, the given <paramref name="handlers"/>, and a Path-D
    /// <see cref="FileSecretSourceFactory"/> rooted at <paramref name="secretsRootDir"/>.
    /// </summary>
    public static IActCommandDispatcher SelectDispatcher(
        bool live,
        string actionsDir,
        string secretsRootDir,
        IEnumerable<IActionExecutor> handlers)
    {
        if (!live)
            return new NullActCommandDispatcher(); // dormant: deny-by-default, builds nothing

        var definitions = ExecutorActionLoader.LoadDirectory(actionsDir);
        var registry = new InMemoryActionExecutorRegistry(handlers);
        var secrets = new FileSecretSourceFactory(secretsRootDir);
        return new ExecutorDispatcher(definitions, registry, secrets);
    }
}
