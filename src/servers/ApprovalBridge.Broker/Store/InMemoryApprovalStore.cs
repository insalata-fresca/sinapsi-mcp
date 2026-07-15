using ApprovalBridge.Broker.Model;

namespace ApprovalBridge.Broker.Store;

/// <summary>
/// In-memory <see cref="IApprovalStore"/> that faithfully models JetStream KV revision-CAS: every
/// write bumps a monotonic revision, and a consume/terminate succeeds only when the caller's expected
/// revision still matches the stored one. This is what makes the one-shot proof real — the same CAS
/// discipline as the JetStream-backed store, exercised deterministically without a live bus. Used by
/// the broker's tests and as the shadow-mode default (nothing durable matters while dispatch is
/// deny-by-default).
/// </summary>
internal sealed class InMemoryApprovalStore : IApprovalStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (PendingEntry Value, ulong Revision)> _kv = new(StringComparer.Ordinal);

    public Task<StoredEntry> CreatePendingAsync(PendingEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (_kv.ContainsKey(entry.RequestId))
                throw new InvalidOperationException($"request_id '{entry.RequestId}' already exists");
            var pending = entry with { Status = RequestStatus.Pending };
            _kv[entry.RequestId] = (pending, 1);
            return Task.FromResult(new StoredEntry(pending, 1));
        }
    }

    public Task<StoredEntry?> GetAsync(string requestId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_kv.TryGetValue(requestId, out var e) ? new StoredEntry(e.Value, e.Revision) : null);
        }
    }

    public Task<bool> TryConsumeAsync(string requestId, ulong expectedRevision, string approverIdentity, CancellationToken ct = default)
        => Task.FromResult(Cas(requestId, expectedRevision, RequestStatus.Consumed, approverIdentity));

    public Task<bool> TryTerminateAsync(string requestId, ulong expectedRevision, RequestStatus terminal, string approverIdentity, CancellationToken ct = default)
        => Task.FromResult(Cas(requestId, expectedRevision, terminal, approverIdentity));

    public Task<IReadOnlyList<StoredEntry>> ListPendingAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<StoredEntry> pending = _kv.Values
                .Where(e => e.Value.Status == RequestStatus.Pending)
                .Select(e => new StoredEntry(e.Value, e.Revision))
                .ToList();
            return Task.FromResult(pending);
        }
    }

    // The atomic CAS: succeed only if the revision is unchanged AND the entry is still pending.
    private bool Cas(string requestId, ulong expectedRevision, RequestStatus next, string approver)
    {
        lock (_gate)
        {
            if (!_kv.TryGetValue(requestId, out var e)) return false;
            if (e.Revision != expectedRevision) return false;          // another writer moved it
            if (e.Value.Status != RequestStatus.Pending) return false;  // only pending → forward
            var updated = e.Value with { Status = next, ApproverIdentity = approver };
            _kv[requestId] = (updated, e.Revision + 1);
            return true;
        }
    }
}
