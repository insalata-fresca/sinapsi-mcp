using ApprovalBridge.Broker.Model;

namespace ApprovalBridge.Broker.Registry;

/// <summary>
/// Read-only view over the git-backed action allowlist (E1.1, home-server
/// <c>policies/approval-bridge/actions/</c>). The registry is policy-as-code: the broker never
/// mutates it, and an <c>action_id</c> absent here is refused before any operator sees it
/// (deny-by-default, docs/66 §2 I6).
/// </summary>
internal interface IActionRegistry
{
    /// <summary>The registered spec for <paramref name="actionId"/>, or null when it is not
    /// allowlisted (an unregistered action_id is a deny-floor, not an error to negotiate).</summary>
    ActionSpec? Find(string actionId);

    /// <summary>All registered action ids (for health / diagnostics only).</summary>
    IReadOnlyCollection<string> ActionIds { get; }
}
