namespace ApprovalBridge.Broker.Core;

/// <summary>Server-side per-agent / per-action request caps (docs/66 §8 T5 — overwhelming-HITL
/// defence). A cap is enforced by the broker, never by trusting the caller to self-limit.</summary>
internal interface IRateLimiter
{
    /// <summary>Try to admit one request for <paramref name="actionId"/> from
    /// <paramref name="requesterIdentity"/>. Returns false when either the per-agent or per-action
    /// rolling-hour cap is already reached. A successful admit consumes one unit.</summary>
    bool TryAdmit(string actionId, string requesterIdentity, int perAgentPerHour, int perActionPerHour, DateTimeOffset now);
}
