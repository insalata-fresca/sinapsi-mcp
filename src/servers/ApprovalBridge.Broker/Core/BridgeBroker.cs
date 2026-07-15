using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApprovalBridge.Broker.Events;
using ApprovalBridge.Broker.Model;
using ApprovalBridge.Broker.Registry;
using ApprovalBridge.Broker.Store;
using Json.Schema;
using Sinapsi.Nats.EventPlane;

namespace ApprovalBridge.Broker.Core;

/// <summary>
/// The Operator Approval Bridge broker (home-server <c>docs/66 §3</c>, E1.3). It holds coordination
/// authority — which request is approved — but NEVER a target secret (the seal is structural: the
/// broker only ever dispatches <c>action_id</c> + validated params to an executor; the secret lives
/// target-side, I2). It runs in SHADOW: dispatch goes through the deny-by-default
/// <see cref="IActCommandDispatcher"/> seam (<see cref="NullActCommandDispatcher"/>), so it acts on nothing.
///
/// <para>The three security invariants are enforced here, server-side, not on trust:
/// <list type="bullet">
/// <item>I3 one-shot — nonce + short expiry + ATOMIC CAS <c>pending→consumed</c> BEFORE dispatch;</item>
/// <item>I7/T1 self-approval — <c>approver_identity != requester_identity</c>, checked structurally;</item>
/// <item>deny-by-default — unknown action / bad params / lost CAS / unwired executor all fail closed.</item>
/// </list></para>
/// </summary>
internal sealed class BridgeBroker
{
    private readonly IActionRegistry _registry;
    private readonly IApprovalStore _store;
    private readonly IApprovalEventEmitter _emitter;
    private readonly IActCommandDispatcher _dispatcher;
    private readonly IRateLimiter _rateLimiter;
    private readonly TimeProvider _clock;
    private readonly INonceSource _nonce;

    public BridgeBroker(
        IActionRegistry registry,
        IApprovalStore store,
        IApprovalEventEmitter emitter,
        IActCommandDispatcher dispatcher,
        IRateLimiter rateLimiter,
        TimeProvider clock,
        INonceSource nonce)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _nonce = nonce ?? throw new ArgumentNullException(nameof(nonce));
    }

    /// <summary>
    /// REQUEST intake (the EVENT — a fact). Validate <paramref name="actionId"/> against the allowlist and
    /// <paramref name="paramsJson"/> against its <c>param_schema</c>; refuse malformed requests immediately
    /// (deny-by-default) BEFORE any operator sees them. On accept, mint nonce + expiry, write KV <c>pending</c>,
    /// and emit <c>...requested</c>. This event does NOT trigger execution (docs/66 §3.1).
    /// </summary>
    public async Task<RequestOutcome> RequestAsync(string actionId, string paramsJson, string requesterIdentity, CancellationToken ct = default)
    {
        var spec = _registry.Find(actionId);
        if (spec is null)
            return RequestOutcome.Deny(BrokerRejectReason.UnknownAction);      // not allowlisted → refused pre-operator

        if (!TryValidateParams(spec.ParamSchema, paramsJson, out var canonicalParams))
            return RequestOutcome.Deny(BrokerRejectReason.ParamsSchemaViolation);

        var now = _clock.GetUtcNow();
        if (!_rateLimiter.TryAdmit(actionId, requesterIdentity, spec.RateLimit.PerAgentPerHour, spec.RateLimit.PerActionPerHour, now))
            return RequestOutcome.Deny(BrokerRejectReason.RateLimited);

        var requestId = Guid.NewGuid().ToString("N");
        var digest = Sha256Hex(canonicalParams);
        var entry = new PendingEntry(
            RequestId: requestId,
            ActionId: actionId,
            ParamsJson: canonicalParams,
            ParamsDigest: digest,
            RequesterIdentity: requesterIdentity,
            Nonce: _nonce.Generate(),
            ExpiresAt: now.AddSeconds(spec.ExpirySeconds),
            Status: RequestStatus.Pending,
            ApproverIdentity: string.Empty);

        await _store.CreatePendingAsync(entry, ct);
        await EmitAsync(spec, entry, "requested", "request recorded; awaiting operator", resultStatus: null, ct);
        return RequestOutcome.Ok(requestId);
    }

    /// <summary>
    /// APPROVE (the COMMAND — single receiver, rejectable). Enforce structurally that the approver is not
    /// the requester (I7/T1), that the request is <c>pending</c> and unexpired with the server-held nonce,
    /// then ATOMIC-CAS <c>pending→consumed</c> BEFORE dispatch (I3). Only the winning CAS dispatches, via the
    /// deny-by-default seam — so exactly one execution is ever attempted and, with no executor wired, nothing acts.
    /// </summary>
    public async Task<ApprovalOutcome> ApproveAsync(string requestId, string approverIdentity, string? presentedNonce = null, CancellationToken ct = default)
    {
        var stored = await _store.GetAsync(requestId, ct);
        if (stored is null) return ApprovalOutcome.Deny(BrokerRejectReason.UnknownRequest);
        var e = stored.Value;

        if (e.Status != RequestStatus.Pending) return ApprovalOutcome.Deny(BrokerRejectReason.NotPending); // replay finds consumed/rejected/expired
        if (e.IsExpiredAt(_clock.GetUtcNow())) return ApprovalOutcome.Deny(BrokerRejectReason.Expired);

        // Structural self-approval block (I7/T1): the requesting identity can never approve its own request.
        if (string.IsNullOrEmpty(approverIdentity) || string.Equals(approverIdentity, e.RequesterIdentity, StringComparison.Ordinal))
            return ApprovalOutcome.Deny(BrokerRejectReason.SelfApproval);

        // Server-held nonce must exist; if the caller presents one it must match (defense-in-depth).
        if (string.IsNullOrEmpty(e.Nonce) || (presentedNonce is not null && !FixedEquals(presentedNonce, e.Nonce)))
            return ApprovalOutcome.Deny(BrokerRejectReason.NonceMismatch);

        var spec = _registry.Find(e.ActionId);
        if (spec is null) return ApprovalOutcome.Deny(BrokerRejectReason.UnknownAction); // de-registered since request → fail closed

        // ATOMIC CAS pending→consumed, BEFORE dispatch. The one-shot pivot (I3/T3).
        if (!await _store.TryConsumeAsync(requestId, stored.Revision, approverIdentity, ct))
            return ApprovalOutcome.Deny(BrokerRejectReason.CasLost); // a concurrent/replayed approval already won

        var approved = e with { Status = RequestStatus.Consumed, ApproverIdentity = approverIdentity };
        await EmitAsync(spec, approved, "approved", "nonce consumed (one-shot)", resultStatus: null, ct);

        // Dispatch through the C2 deny-by-default seam. Carries NO secret — only action_id/target/correlation.
        var command = new ActCommand(
            CommandId: Guid.NewGuid().ToString("N"),
            Kind: ActCommandKind.ApprovalBridgeExecute,
            Target: spec.TargetHost,
            CorrelationId: requestId,
            RequestedBy: $"operator:{approverIdentity}",
            Reason: "approved; one-shot nonce consumed; executed under target identity");
        var ack = await _dispatcher.DispatchAsync(command, ct);

        var executedReason = ack.Accepted
            ? "executor accepted; ran under target identity"
            : $"deny-by-default: {ack.Reason}; nothing acted";
        await EmitAsync(spec, approved, "executed", executedReason, resultStatus: ack.Accepted ? "ok" : null, ct);

        return new ApprovalOutcome(Accepted: true, Dispatched: true, ExecutorAccepted: ack.Accepted, BrokerRejectReason.None, ack.Reason);
    }

    /// <summary>REJECT (the COMMAND): the operator declines a pending request. CAS <c>pending→rejected</c>
    /// and emit <c>...rejected</c>. Safe (never dispatches).</summary>
    public async Task<ApprovalOutcome> RejectAsync(string requestId, string approverIdentity, CancellationToken ct = default)
    {
        var stored = await _store.GetAsync(requestId, ct);
        if (stored is null) return ApprovalOutcome.Deny(BrokerRejectReason.UnknownRequest);
        if (stored.Value.Status != RequestStatus.Pending) return ApprovalOutcome.Deny(BrokerRejectReason.NotPending);

        if (!await _store.TryTerminateAsync(requestId, stored.Revision, RequestStatus.Rejected, approverIdentity, ct))
            return ApprovalOutcome.Deny(BrokerRejectReason.CasLost);

        var spec = _registry.Find(stored.Value.ActionId);
        if (spec is not null)
        {
            var rejected = stored.Value with { Status = RequestStatus.Rejected, ApproverIdentity = approverIdentity };
            await EmitAsync(spec, rejected, "rejected", "operator rejected", resultStatus: null, ct);
        }
        return new ApprovalOutcome(Accepted: true, Dispatched: false, ExecutorAccepted: false, BrokerRejectReason.None, "rejected");
    }

    /// <summary>Expiry reaper: CAS every due <c>pending</c> entry to <c>expired</c> and emit <c>...expired</c>.
    /// An expired request can never be approved (docs/66 §5.2). Returns the count expired.</summary>
    public async Task<int> ExpireDueAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var expired = 0;
        foreach (var stored in await _store.ListPendingAsync(ct))
        {
            if (!stored.Value.IsExpiredAt(now)) continue;
            if (!await _store.TryTerminateAsync(stored.Value.RequestId, stored.Revision, RequestStatus.Expired, string.Empty, ct)) continue;
            var spec = _registry.Find(stored.Value.ActionId);
            if (spec is not null)
                await EmitAsync(spec, stored.Value with { Status = RequestStatus.Expired }, "expired", "one-shot window elapsed", resultStatus: null, ct);
            expired++;
        }
        return expired;
    }

    private ValueTask EmitAsync(ActionSpec spec, PendingEntry e, string verdict, string reason, string? resultStatus, CancellationToken ct)
    {
        var envelope = BridgeEnvelope.Build(
            actionId: spec.ActionId,
            verdict: verdict,
            target: $"{spec.TargetHost} ({spec.TargetIdentity})",
            requester: e.RequesterIdentity,
            approver: e.ApproverIdentity,
            reason: reason,
            paramsDigest: e.ParamsDigest,
            resultStatus: resultStatus,
            correlationId: e.RequestId);
        return _emitter.EmitAsync(new ApprovalFact(spec.ActionId, verdict, envelope, e.RequestId), ct);
    }

    // Validate params against the action's param_schema; canonicalise to a stable JSON string for the digest.
    private static bool TryValidateParams(JsonSchema schema, string paramsJson, out string canonical)
    {
        canonical = string.Empty;
        JsonNode? node;
        try { node = JsonNode.Parse(string.IsNullOrWhiteSpace(paramsJson) ? "{}" : paramsJson); }
        catch (JsonException) { return false; }
        var result = schema.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.Flag });
        if (!result.IsValid) return false;
        canonical = node?.ToJsonString() ?? "{}";
        return true;
    }

    private static string Sha256Hex(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    // Constant-time compare so a nonce check cannot be timing-probed.
    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
