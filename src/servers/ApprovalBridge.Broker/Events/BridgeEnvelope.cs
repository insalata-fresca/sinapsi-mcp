using System.Text.Json.Nodes;
using Sinapsi.Nats.EventPlane;

namespace ApprovalBridge.Broker.Events;

/// <summary>
/// The Operator Approval Bridge decision envelope (home-server <c>docs/66 §9</c>) — the <c>layer:"bridge"</c>
/// extension of the common decision envelope (<c>docs/61 §8</c>) that the C2
/// <see cref="DecisionEnvelopeContract"/> checks for the authz plane. It is emitted as a FACT on
/// <c>homelab.security.approval.&lt;action_id&gt;.&lt;verdict&gt;</c>, joined by <c>correlation_id == request_id</c>
/// so the Console shows the full requested→approved→executed chain (I4).
///
/// <para>The audit carries <c>params_digest</c> (a SHA-256), never the raw params, and NEVER the request's
/// free-text rationale — keeping the stream free of anything sensitive or injection-bearing (docs/66 §9, T4).</para>
/// </summary>
internal static class BridgeEnvelope
{
    public const string Layer = "bridge";
    public const string Question = "operator-approval";
    public const string Surface = "approval-bridge";

    /// <summary>Root of the approval FACT subject tree (a sibling of <c>homelab.security.authz.&gt;</c>,
    /// under the existing <c>homelab.security.&gt;</c> audited domain).</summary>
    public const string FactSubjectRoot = "homelab.security.approval";

    /// <summary>The closed verdict vocabulary. Anything outside this is unclassifiable → DLQ.</summary>
    public static readonly IReadOnlySet<string> Verdicts =
        new HashSet<string>(StringComparer.Ordinal) { "requested", "approved", "rejected", "executed", "expired" };

    public static bool IsClassifiable(string verdict) => !string.IsNullOrEmpty(verdict) && Verdicts.Contains(verdict);

    /// <summary>The NATS subject for a bridge fact: <c>homelab.security.approval.&lt;action_id&gt;.&lt;verdict&gt;</c>.</summary>
    public static string SubjectFor(string actionId, string verdict) => $"{FactSubjectRoot}.{actionId}.{verdict}";

    /// <summary>Build the CloudEvent <c>data</c> payload for one bridge step. String fields that a step
    /// legitimately lacks (e.g. <c>approver</c> before approval, <c>result_status</c> until executed) are
    /// emitted empty/null — never omitted — so the shape is stable across the chain.</summary>
    public static JsonObject Build(
        string actionId,
        string verdict,
        string target,
        string requester,
        string approver,
        string reason,
        string paramsDigest,
        string? resultStatus,
        string correlationId)
        => new()
        {
            ["layer"] = Layer,
            ["question"] = Question,
            ["surface"] = Surface,
            ["action_id"] = actionId,
            ["target"] = target,
            ["requester"] = requester,       // provenance; untrusted request text is NOT included
            ["approver"] = approver,         // "" until approved; set on the COMMAND
            ["verdict"] = verdict,
            ["reason"] = reason,
            ["params_digest"] = paramsDigest, // sha256 of validated params — integrity, not the values
            ["result_status"] = resultStatus is null ? null : JsonValue.Create(resultStatus),
            ["correlation_id"] = correlationId, // == request_id; threads requested→approved→executed
        };
}
