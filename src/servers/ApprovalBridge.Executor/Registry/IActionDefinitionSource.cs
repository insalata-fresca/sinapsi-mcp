namespace ApprovalBridge.Executor.Registry;

/// <summary>Read-only lookup of executor-side action definitions loaded from the git-backed allowlist
/// (E1.1). An <c>action_id</c> absent here is refused by the dispatcher before any handler runs
/// (deny-by-default — the allowlist is the deny floor, home-server <c>docs/66 §2 I6</c>).</summary>
public interface IActionDefinitionSource
{
    /// <summary>The definition for <paramref name="actionId"/>, or null when it is not allowlisted.</summary>
    ExecutorActionDefinition? Find(string actionId);
}
