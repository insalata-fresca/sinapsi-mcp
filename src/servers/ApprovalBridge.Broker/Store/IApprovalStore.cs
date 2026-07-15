using ApprovalBridge.Broker.Model;

namespace ApprovalBridge.Broker.Store;

/// <summary>
/// The durable pending-approval state (docs/66 §3.1: JetStream KV bucket <c>APPROVAL_REQUESTS</c>).
/// The one safety-critical operation is <see cref="TryConsumeAsync"/>: an ATOMIC compare-and-swap on
/// the KV revision that transitions <c>pending → consumed</c> and can win at most once, so exactly one
/// approval ever dispatches even under concurrent or replayed approvals (docs/66 §5.3, I3/T3). The
/// bus is never the source of truth for this — the KV CAS is (docs/64 §3).
/// </summary>
internal interface IApprovalStore
{
    /// <summary>Write a freshly-minted <c>pending</c> entry. Fails if the key already exists (the
    /// request_id is unique). Returns the entry with its initial KV revision.</summary>
    Task<StoredEntry> CreatePendingAsync(PendingEntry entry, CancellationToken ct = default);

    /// <summary>Read the current entry + revision, or null when absent.</summary>
    Task<StoredEntry?> GetAsync(string requestId, CancellationToken ct = default);

    /// <summary>
    /// Atomic one-shot consume: CAS <c>pending → consumed</c> only if the stored revision still equals
    /// <paramref name="expectedRevision"/>. Returns true for the single winning caller; false when the
    /// revision moved (a concurrent/replayed approval already consumed it). This is the replay defense —
    /// server-side, not trust (docs/66 §5.3).
    /// </summary>
    Task<bool> TryConsumeAsync(string requestId, ulong expectedRevision, string approverIdentity, CancellationToken ct = default);

    /// <summary>Transition <c>pending → </c> a terminal non-consumed state (<c>rejected</c>/<c>expired</c>)
    /// via CAS on the revision. Returns false if the revision moved. Never consumes for dispatch.</summary>
    Task<bool> TryTerminateAsync(string requestId, ulong expectedRevision, RequestStatus terminal, string approverIdentity, CancellationToken ct = default);

    /// <summary>Snapshot of currently-<c>pending</c> entries (for the expiry reaper).</summary>
    Task<IReadOnlyList<StoredEntry>> ListPendingAsync(CancellationToken ct = default);
}
