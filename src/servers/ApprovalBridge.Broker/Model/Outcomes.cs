namespace ApprovalBridge.Broker.Model;

/// <summary>Why the broker refused, or accepted, a step. Every refusal is deny-by-default: the
/// broker fails closed on anything it cannot positively validate (docs/66 §8).</summary>
internal enum BrokerRejectReason
{
    None,
    UnknownAction,          // action_id not in the git-backed allowlist (I6)
    ParamsSchemaViolation,  // params failed the action's param_schema (deny-by-default)
    RateLimited,            // per-agent / per-action cap hit (T5)
    UnknownRequest,         // no such request_id
    NotPending,             // already consumed / rejected / expired (replay finds this — I3/T3)
    Expired,                // one-shot window elapsed
    SelfApproval,           // approver_identity == requester_identity (I7/T1 — structural block)
    NonceMismatch,          // presented nonce did not match the server-held one (defense-in-depth)
    CasLost,                // another approval won the atomic CAS first (exactly-one-execution — I3)
}

/// <summary>Result of request intake.</summary>
/// <param name="Accepted">True when a pending entry was minted and <c>...requested</c> emitted.</param>
/// <param name="RequestId">The opaque id the operator will approve (empty on rejection).</param>
/// <param name="Reason">Why it was refused (<see cref="BrokerRejectReason.None"/> on accept).</param>
internal sealed record RequestOutcome(bool Accepted, string RequestId, BrokerRejectReason Reason)
{
    public static RequestOutcome Ok(string requestId) => new(true, requestId, BrokerRejectReason.None);
    public static RequestOutcome Deny(BrokerRejectReason reason) => new(false, string.Empty, reason);
}

/// <summary>Result of an approve command. <see cref="Dispatched"/> is true only when the atomic CAS
/// consume won AND the act-command reached the dispatcher; <see cref="ExecutorAccepted"/> reflects the
/// dispatcher's ack — deny-by-default (<c>NullActCommandDispatcher</c>) rejects it, so nothing acts.</summary>
/// <param name="Accepted">True when this approval won the one-shot CAS.</param>
/// <param name="Dispatched">True when the act-command was handed to the dispatcher seam.</param>
/// <param name="ExecutorAccepted">The dispatcher's disposition (false under deny-by-default).</param>
/// <param name="Reason">Refusal reason, or the dispatcher's reject reason.</param>
/// <param name="ResultJson">The executor's NON-SECRET result (conforming to <c>result_schema</c>), or null when
/// nothing executed (deny-by-default) — never a secret or token (docs/66 §3.4, I2).</param>
internal sealed record ApprovalOutcome(bool Accepted, bool Dispatched, bool ExecutorAccepted, BrokerRejectReason Reason, string Detail, string? ResultJson = null)
{
    public static ApprovalOutcome Deny(BrokerRejectReason reason) => new(false, false, false, reason, string.Empty);
}
