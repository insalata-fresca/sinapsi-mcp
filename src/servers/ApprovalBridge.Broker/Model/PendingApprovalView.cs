using System.Text.Json.Nodes;

namespace ApprovalBridge.Broker.Model;

/// <summary>
/// The operator-facing projection of one <see cref="PendingEntry"/>, joined with its registry
/// <see cref="ActionSpec"/> (home-server <c>docs/66 §6</c> step 3 / <c>§9</c> T4). This is the
/// READ-ONLY shape the Sentinel Console renders as a pending-approval card: the registry
/// <see cref="Title"/> + the TYPED, schema-validated <see cref="Params"/> + provenance
/// (<see cref="RequesterIdentity"/>, <see cref="ActionId"/>, <see cref="ExpiresAt"/>) — never the
/// requester's free-text rationale, because none is carried anywhere in the broker's model to begin
/// with (docs/66 §8 T4: the executor and the audit trail consume only <c>action_id</c> + schema-
/// validated params, never prose).
///
/// <para>This view is a pure projection — listing it performs no state transition and enforces
/// nothing; the one-shot / self-approval / CAS checks live exclusively in
/// <see cref="Core.BridgeBroker.ApproveAsync"/> / <see cref="Core.BridgeBroker.RejectAsync"/>. A
/// consumer cannot approve anything by reading this list.</para>
/// </summary>
/// <param name="RequestId">Opaque id the operator approves/rejects (the correlation id).</param>
/// <param name="ActionId">The registered, allowlisted action this request is for.</param>
/// <param name="Title">Operator-facing title from the registry — never agent free text.</param>
/// <param name="Description">Operator-facing description from the registry.</param>
/// <param name="Params">The typed, schema-validated params (parsed back from the stored canonical JSON).</param>
/// <param name="RequesterIdentity">Provenance: the agent identity that issued the request.</param>
/// <param name="ExpiresAt">The one-shot approval window's deadline; unapprovable past this.</param>
/// <param name="RiskTier"><c>green</c> | <c>yellow</c> | <c>red</c>, from the registry.</param>
internal sealed record PendingApprovalView(
    string RequestId,
    string ActionId,
    string Title,
    string Description,
    JsonNode? Params,
    string RequesterIdentity,
    DateTimeOffset ExpiresAt,
    string RiskTier);
