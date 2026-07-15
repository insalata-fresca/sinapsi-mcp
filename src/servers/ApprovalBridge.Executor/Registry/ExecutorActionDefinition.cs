using Json.Schema;

namespace ApprovalBridge.Executor.Registry;

/// <summary>
/// The slice of a git-backed allowlist entry (E1.1
/// <c>policies/approval-bridge/actions/&lt;action_id&gt;.yaml</c>) that the <b>executor</b> needs, loaded
/// target-side and read-only (home-server <c>docs/66 §4</c>: "loads the action definition from the
/// allowlist"). The broker parses the same file for its own concerns; the executor is a separate target-side
/// component and loads its own copy — it must not depend on the broker's internals.
///
/// <para>Crucially this captures <see cref="ResultSchema"/>, which the executor uses to prove the returned
/// result carries only the non-secret shape (no token/secret field can pass, I2). It also carries the
/// <see cref="TargetIdentity"/> the executor runs as and the <see cref="ExecutorName"/> that binds the
/// action to its handler.</para>
/// </summary>
/// <param name="ActionId">Stable dotted id (equals the filename stem).</param>
/// <param name="ExecutorName">The <c>executor:</c> handler name bound to this action.</param>
/// <param name="TargetIdentity">The target's own scoped identity the executor runs under (I2).</param>
/// <param name="ParamSchema">JSON Schema for the inputs (re-validated by the executor, defense-in-depth).</param>
/// <param name="ResultSchema">JSON Schema for the NON-SECRET result the executor must conform to.</param>
/// <param name="ResultProperties">The exact set of property names <c>result_schema</c> declares. The
/// dispatcher rejects any result carrying a key outside this set — closing the leak that an open
/// <c>additionalProperties</c> would otherwise permit, so the seal (I2) does not depend on the authored
/// schema happening to set <c>additionalProperties:false</c>.</param>
public sealed record ExecutorActionDefinition(
    string ActionId,
    string ExecutorName,
    string TargetIdentity,
    JsonSchema ParamSchema,
    JsonSchema ResultSchema,
    IReadOnlySet<string> ResultProperties);
