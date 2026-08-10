using System.Text.Json.Nodes;

namespace Sinapsi.SentinelConsole;

/// <summary>
/// One normalized Operator Approval Bridge lifecycle event, parsed from a bus CloudEvent on
/// <c>homelab.security.approval.&lt;action_id&gt;.&lt;verdict&gt;</c> (home-server <c>docs/66 §9</c>, the
/// <c>layer:"bridge"</c> extension of the common decision envelope emitted by the E1.3 broker). This is
/// the row the <see cref="ApprovalQueueModel"/> stores for the lifecycle feed and the Console renders,
/// mirroring <see cref="AuthzDecision"/> / <see cref="DeployEvent"/> exactly (a distinct parser because
/// this is a distinct subject family + envelope shape, not because the mechanics differ).
///
/// <para>Note what this record deliberately does NOT carry: the raw request params (only
/// <see cref="ParamsDigest"/>, a sha256) and — critically — no free-text rationale field exists
/// anywhere in the bridge envelope (docs/66 §9, §8 T4: the audit stream is kept free of anything
/// sensitive or injection-bearing). The TYPED params for the currently-open queue come from a
/// separate, broker-proxied source (<see cref="BrokerClient.GetPendingAsync"/>), never from this
/// event.</para>
/// </summary>
public sealed record ApprovalEvent(
    string ActionId,
    string Verdict,         // requested | approved | rejected | executed | expired
    string Target,
    string Requester,       // provenance; the untrusted request text is never included (docs/66 §9)
    string Approver,        // "" until approved/rejected
    string Reason,
    string ParamsDigest,    // sha256 of the validated params — integrity, never the raw values
    string ResultStatus,    // "ok" | "error" | "" (set only on `executed`)
    string CorrelationId,   // == request_id; threads requested→approved→executed
    DateTimeOffset Time)
{
    private const string SubjectPrefix = "homelab.security.approval.";

    public static readonly IReadOnlySet<string> OpenVerdicts =
        new HashSet<string>(StringComparer.Ordinal) { "requested" };

    public static readonly IReadOnlySet<string> TerminalVerdicts =
        new HashSet<string>(StringComparer.Ordinal) { "approved", "rejected", "executed", "expired" };

    /// <summary>
    /// Parse a bridge lifecycle event from a bus CloudEvent (its raw JSON) delivered on
    /// <paramref name="subject"/>. Only <c>homelab.security.approval.&lt;action_id&gt;.&lt;verdict&gt;</c>
    /// subjects are recognized (a sibling of <c>homelab.security.authz.&gt;</c> under the shared
    /// <c>homelab.security.&gt;</c> audited domain). Returns null for anything else, or anything
    /// unparseable — dropped, never thrown (the SentinelConsole bus subscribers never crash on a
    /// malformed message).
    /// </summary>
    public static ApprovalEvent? TryParse(string subject, string json, DateTimeOffset now)
    {
        if (!subject.StartsWith(SubjectPrefix, StringComparison.Ordinal)) return null;

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return null; }
        if (root is null) return null;

        var data = root["data"] as JsonObject;
        if (data is null) return null;
        var time = ParseTime(root["time"]?.GetValue<string>(), now);

        var verdict = Str(data["verdict"]);
        var actionId = Str(data["action_id"]);
        if (verdict.Length == 0 || actionId.Length == 0) return null;

        var correlationId = Str(data["correlation_id"]);
        if (correlationId.Length == 0) return null;   // unjoinable — the chain view depends on this

        return new ApprovalEvent(
            ActionId: actionId,
            Verdict: verdict,
            Target: Str(data["target"]),
            Requester: Str(data["requester"]),
            Approver: Str(data["approver"]),
            Reason: Str(data["reason"]),
            ParamsDigest: Str(data["params_digest"]),
            ResultStatus: Str(data["result_status"]),
            CorrelationId: correlationId,
            Time: time);
    }

    private static string Str(JsonNode? n, string dflt = "")
        => n is null ? dflt : (n.GetValue<object?>() is { } ? n.ToString() : dflt);

    private static DateTimeOffset ParseTime(string? s, DateTimeOffset now)
        => DateTimeOffset.TryParse(s, out var t) ? t : now;
}
