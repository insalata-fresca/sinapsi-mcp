using Json.Schema;

namespace ApprovalBridge.Broker.Model;

/// <summary>Server-side rate caps for one allowlisted action (docs/66 §8 T5 — HITL-overwhelm).</summary>
/// <param name="PerAgentPerHour">Max requests this action accepts from ONE requester identity per hour.</param>
/// <param name="PerActionPerHour">Max requests this action accepts in total per hour.</param>
public sealed record RateLimit(int PerAgentPerHour, int PerActionPerHour);

/// <summary>
/// One registered, requestable action from the git-backed allowlist
/// (home-server <c>policies/approval-bridge/actions/&lt;action_id&gt;.yaml</c>, E1.1). The broker
/// (E1.3) treats the registry as read-only policy-as-code: an approval authorizes exactly this
/// pre-registered scoped action with params validated against <see cref="ParamSchema"/> — never an
/// arbitrary command (docs/66 §2, I6).
/// </summary>
/// <param name="ActionId">Stable dotted id (e.g. <c>garmin.oauth.exchange</c>).</param>
/// <param name="Title">Operator-facing title the Console renders — never the agent's free text.</param>
/// <param name="Description">Operator-facing description of exactly what the action does.</param>
/// <param name="TargetHost">Host the executor runs on (<c>ct&lt;NNN&gt;-&lt;name&gt;</c>).</param>
/// <param name="TargetIdentity">The TARGET's own scoped identity — never the requester's (I2, the seal).</param>
/// <param name="Executor">Name of the pre-deployed handler bound to this action_id (E1.4, unbuilt).</param>
/// <param name="ParamSchema">JSON Schema for the typed inputs; deny-by-default (additionalProperties:false).</param>
/// <param name="RiskTier"><c>green</c> | <c>yellow</c> | <c>red</c> — feeds the deny-floor + rate policy.</param>
/// <param name="ExpirySeconds">One-shot approval window; a request older than this can never be approved.</param>
/// <param name="RateLimit">Per-agent / per-action server-side caps.</param>
/// <param name="OneShot">Must be true (v1 invariant — no standing grants).</param>
internal sealed record ActionSpec(
    string ActionId,
    string Title,
    string Description,
    string TargetHost,
    string TargetIdentity,
    string Executor,
    JsonSchema ParamSchema,
    string RiskTier,
    int ExpirySeconds,
    RateLimit RateLimit,
    bool OneShot);
