namespace ApprovalBridge.Broker.Model;

/// <summary>Lifecycle of one approval request in the KV bucket <c>APPROVAL_REQUESTS</c>. The
/// broker only ever transitions <see cref="Pending"/> forward, and only via an atomic CAS on the
/// KV revision (docs/66 §5, I3 one-shot).</summary>
internal enum RequestStatus
{
    /// <summary>Minted, awaiting an operator command. The ONLY state from which approval can win.</summary>
    Pending,
    /// <summary>The one winning CAS consumed this request before dispatch — replays now find this.</summary>
    Consumed,
    /// <summary>The operator rejected the pending request.</summary>
    Rejected,
    /// <summary>The one-shot window elapsed before approval — can never be approved.</summary>
    Expired,
}

/// <summary>
/// The durable pending-approval record. The <see cref="Nonce"/> is minted server-side and stored
/// ONLY here (docs/66 §5.1): the operator UI carries only the opaque <see cref="RequestId"/>, so the
/// approving human never handles the nonce, and a replayed/forged approval that cannot present a
/// live pending entry with the server-held nonce is refused.
/// </summary>
/// <param name="RequestId">Opaque id joining requested→approved→executed (the correlation id).</param>
/// <param name="ActionId">The registered action this request is for.</param>
/// <param name="ParamsJson">The typed params, already validated against the action's param_schema.</param>
/// <param name="ParamsDigest">SHA-256 of <see cref="ParamsJson"/> — the audit carries the digest, never raw params.</param>
/// <param name="RequesterIdentity">The agent identity that issued the request (provenance).</param>
/// <param name="Nonce">Cryptographically-random one-shot token, held server-side only.</param>
/// <param name="ExpiresAt">now + action.expiry_seconds; past this the request is un-approvable.</param>
/// <param name="Status">Where in the lifecycle this request sits.</param>
/// <param name="ApproverIdentity">Set on the approve/reject command; empty until then.</param>
internal sealed record PendingEntry(
    string RequestId,
    string ActionId,
    string ParamsJson,
    string ParamsDigest,
    string RequesterIdentity,
    string Nonce,
    DateTimeOffset ExpiresAt,
    RequestStatus Status,
    string ApproverIdentity)
{
    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;
}

/// <summary>A <see cref="PendingEntry"/> paired with its KV revision — the CAS token for one-shot
/// consume (JetStream KV update succeeds only when the expected revision still matches).</summary>
/// <param name="Value">The stored entry.</param>
/// <param name="Revision">The KV revision this snapshot was read at.</param>
internal sealed record StoredEntry(PendingEntry Value, ulong Revision);
