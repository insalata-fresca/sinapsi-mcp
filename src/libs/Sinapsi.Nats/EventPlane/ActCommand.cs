namespace Sinapsi.Nats.EventPlane;

/// <summary>The kind of act a <see cref="ActCommand"/> requests. The act-path executor
/// (auto-merge / auto-deploy) is out of scope here — this enum names the seam so the
/// command contract is concrete and testable before the executor exists.</summary>
public enum ActCommandKind
{
    /// <summary>Merge an approved pull request.</summary>
    MergePullRequest,
    /// <summary>Deploy an already-merged, CI-green change.</summary>
    Deploy,
    /// <summary>Execute a target-side handler for an operator-approved, allowlisted action
    /// (the Operator Approval Bridge act-path, home-server <c>docs/66 §3.4</c>). Additive: the
    /// approval-bridge broker (E1.3) reuses this same rejectable act-command seam and its
    /// deny-by-default <see cref="NullActCommandDispatcher"/> so the broker can dispatch an
    /// approved action to an executor without holding any target secret. The executor itself
    /// (E1.4) is not built — the null dispatcher rejects every kind, so this dispatches nothing.</summary>
    ApprovalBridgeExecute,
}

/// <summary>
/// A rejectable ACT command (home-server <c>docs/64 §3</c>) — the COMMAND half of the
/// verdict-fact / act-command split. Unlike a verdict FACT (pub/sub, many consumers, not a
/// trigger), a command is addressed to a SINGLE receiver, delivered once via a work-queue,
/// and may be refused. It carries the <see cref="CorrelationId"/> of the verdict that
/// justified it so the read-model can join the decision to the action — but the verdict
/// event does not <i>produce</i> this command; a distinct decide→act seam (an outbox, a
/// deferred follow-on) does.
/// </summary>
/// <param name="CommandId">Unique id for this command instance (idempotency key for the
/// single receiver).</param>
/// <param name="Kind">What to do.</param>
/// <param name="Target">What to act on (e.g. <c>ste/sinapsi-mcp#123</c>, a deploy target).</param>
/// <param name="CorrelationId">The verdict/request trace id this act descends from.</param>
/// <param name="RequestedBy">The identity that issued the command (audit).</param>
/// <param name="Reason">Human-readable justification (audit).</param>
/// <param name="Payload">Optional act-payload. For <see cref="ActCommandKind.ApprovalBridgeExecute"/>
/// this carries the <c>action_id</c> + the schema-validated, NON-SECRET params the target-side
/// executor needs to run (home-server <c>docs/66 §3.4</c>: "dispatches (action_id, params) … over an
/// authenticated channel carrying no secret"). It is additive and defaults to <c>null</c> so the
/// merge-pr / deploy kinds — and any pre-existing caller — are unaffected. It never carries a target
/// secret: the seal is that the secret is read target-side by the executor, never dispatched (I2).</param>
public sealed record ActCommand(
    string CommandId,
    ActCommandKind Kind,
    string Target,
    string CorrelationId,
    string RequestedBy,
    string Reason,
    ActPayload? Payload = null)
{
    /// <summary>The work-queue subject this command belongs on, under
    /// <see cref="EventPlaneChannels.ActCommandSubjectRoot"/> — never a verdict-fact subject.</summary>
    public string Subject => $"{EventPlaneChannels.ActCommandSubjectRoot}.{KindSlug(Kind)}";

    internal static string KindSlug(ActCommandKind kind) => kind switch
    {
        ActCommandKind.MergePullRequest => "merge-pr",
        ActCommandKind.Deploy => "deploy",
        ActCommandKind.ApprovalBridgeExecute => "approval-execute",
        _ => "unknown",
    };
}

/// <summary>
/// The NON-SECRET act-payload of an <see cref="ActCommand"/> for the Operator Approval Bridge
/// (<see cref="ActCommandKind.ApprovalBridgeExecute"/>). It carries exactly what the target-side
/// executor needs to run a pre-registered scoped action — the allowlisted <paramref name="ActionId"/>
/// and the server-side-validated <paramref name="ParamsJson"/> — and NOTHING secret. The target's own
/// secret (e.g. the Garmin client secret) is read target-side by the executor via register-secret
/// Path D and never travels in this payload: that is what makes the seal structural (home-server
/// <c>docs/66 §3, §4</c>, I2). Params here are always the canonical, <c>param_schema</c>-validated form.
/// </summary>
/// <param name="ActionId">The allowlisted action id the executor must run (e.g. <c>garmin.oauth.exchange</c>).</param>
/// <param name="ParamsJson">The canonical, schema-validated, non-secret params JSON (e.g. <c>{"auth_code":"…"}</c>).</param>
public sealed record ActPayload(string ActionId, string ParamsJson);

/// <summary>Whether the single receiver accepted or rejected an <see cref="ActCommand"/>.</summary>
public enum ActCommandDisposition
{
    /// <summary>The receiver accepted the command and will (or did) act.</summary>
    Accepted,
    /// <summary>The receiver refused the command — a command is rejectable by contract.</summary>
    Rejected,
}

/// <summary>The single receiver's answer to a dispatched <see cref="ActCommand"/>. A command
/// is rejectable: <see cref="Rejected"/> carries why. (This is what a pub/sub FACT cannot do —
/// a fact has no addressee and no answer.)</summary>
/// <param name="Disposition">Accepted or Rejected.</param>
/// <param name="Reason">Why (required on rejection; may be empty on acceptance).</param>
/// <param name="ResultJson">Optional NON-SECRET result the receiver produced, conforming to the action's
/// <c>result_schema</c> (home-server <c>docs/66 §3.4</c>). For the Operator Approval Bridge this is the
/// <c>{status, stored, expires_at}</c> confirmation the executor returns — NEVER the token or any secret.
/// Additive, defaults to <c>null</c>; the merge-pr / deploy kinds leave it null.</param>
public sealed record ActCommandAck(ActCommandDisposition Disposition, string Reason, string? ResultJson = null)
{
    public bool Accepted => Disposition == ActCommandDisposition.Accepted;

    public static ActCommandAck Accept(string reason = "") => new(ActCommandDisposition.Accepted, reason);

    /// <summary>Accept and carry the executor's non-secret <paramref name="resultJson"/> back to the broker.</summary>
    public static ActCommandAck AcceptWithResult(string resultJson, string reason = "") =>
        new(ActCommandDisposition.Accepted, reason, resultJson);

    public static ActCommandAck Reject(string reason) => new(ActCommandDisposition.Rejected, reason);
}

/// <summary>
/// The decide→act seam as a contract: hand a rejectable <see cref="ActCommand"/> to the SINGLE
/// receiver that owns the act, and get back its <see cref="ActCommandAck"/>. Implementations
/// back this with a work-queue (one durable consumer, ack-to-delete) on the
/// <see cref="EventPlaneChannels.ActCommandSubjectRoot"/> tree — never by subscribing to a
/// verdict FACT. The concrete executor is deliberately NOT built here (out of scope); the
/// safe default until it exists is <see cref="NullActCommandDispatcher"/>.
/// </summary>
public interface IActCommandDispatcher
{
    /// <summary>Dispatch <paramref name="command"/> to its single receiver and await the ack.</summary>
    ValueTask<ActCommandAck> DispatchAsync(ActCommand command, CancellationToken ct = default);
}

/// <summary>
/// Fail-safe default dispatcher for while the act-path executor is not built: it REJECTS
/// every command ("no act-path executor wired"). This encodes the deny-by-default posture at
/// the act seam — a verdict can never silently cause a merge/deploy just because no executor
/// is present. Swap it for the real work-queue-backed dispatcher when the executor lands.
/// </summary>
public sealed class NullActCommandDispatcher : IActCommandDispatcher
{
    public const string RejectReason = "no act-path executor wired (deny-by-default at the act seam)";

    public ValueTask<ActCommandAck> DispatchAsync(ActCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        // Guard the invariant even here: the command must be an act-command subject, not a fact.
        EventPlaneChannels.EnsureNotFactTriggered(command.Subject);
        return ValueTask.FromResult(ActCommandAck.Reject(RejectReason));
    }
}
