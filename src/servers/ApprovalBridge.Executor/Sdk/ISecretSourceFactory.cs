using ApprovalBridge.Executor.Registry;

namespace ApprovalBridge.Executor.Sdk;

/// <summary>
/// Builds the Path-D <see cref="ISecretSource"/> for a specific action definition — scoped to that action's
/// <see cref="ExecutorActionDefinition.TargetIdentity"/>. This is where "runs under the target's own identity"
/// (I2) is honoured: the factory hands the handler a secret source bound to the target identity, not the
/// requester's. In production this returns an <c>infisical run</c>- or <c>0600</c>-file-backed source on the
/// target host; in tests it returns a mock. The broker never calls this — it lives entirely target-side.
/// </summary>
public interface ISecretSourceFactory
{
    /// <summary>The Path-D secret source scoped to <paramref name="definition"/>'s target identity.</summary>
    ISecretSource ForTarget(ExecutorActionDefinition definition);
}
