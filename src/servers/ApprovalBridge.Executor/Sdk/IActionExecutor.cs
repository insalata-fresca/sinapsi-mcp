namespace ApprovalBridge.Executor.Sdk;

/// <summary>
/// The generic handler contract at the heart of the Executor SDK (home-server <c>docs/66 §4</c>): a small,
/// pre-deployed handler bound to exactly one allowlisted <see cref="ActionId"/>. Its whole job is
/// <c>validated params in → non-secret result out</c>, reading whatever secret it needs target-side via the
/// injected <see cref="ISecretSource"/> (Path D). It runs under the target's own identity and NEVER returns,
/// logs, or throws a secret.
///
/// <para>A new action type ships a new <see cref="IActionExecutor"/> + an allowlist entry — both reviewed via
/// PR (§4). The <see cref="Dispatch.ExecutorDispatcher"/> is action-agnostic: it resolves the handler by the
/// action definition's <c>executor</c> name and validates the handler's result against <c>result_schema</c>.</para>
/// </summary>
public interface IActionExecutor
{
    /// <summary>The allowlisted <c>executor</c> name this handler is bound to (matches the action definition's
    /// <c>executor:</c> field, e.g. <c>garmin-oauth-exchange</c>). The dispatcher resolves handlers by this name.</summary>
    string ExecutorName { get; }

    /// <summary>Run the action. <paramref name="request"/> carries the non-secret validated params;
    /// <paramref name="secrets"/> is the Path-D source for reading the target's own secret. Return ONLY a
    /// non-secret result conforming to the action's <c>result_schema</c>.</summary>
    Task<ExecutorResult> ExecuteAsync(ExecutorRequest request, ISecretSource secrets, CancellationToken ct = default);
}
