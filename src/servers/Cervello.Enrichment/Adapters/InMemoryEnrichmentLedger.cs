using System.Collections.Concurrent;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// In-memory idempotency ledger (tests + the offline slice). The live CT146 Postgres ledger
/// is an E3 adapter; this keeps the E2 stages testable without a database while enforcing the
/// same "claim once, replay = no-op" contract atomically.
/// </summary>
public sealed class InMemoryEnrichmentLedger : IEnrichmentLedger
{
    private readonly ConcurrentDictionary<string, byte> _claimed = new(StringComparer.Ordinal);

    public Task<bool> TryClaimAsync(string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("idempotencyKey must be non-empty", nameof(idempotencyKey));
        return Task.FromResult(_claimed.TryAdd(idempotencyKey, 0));
    }

    public Task<bool> IsClaimedAsync(string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(_claimed.ContainsKey(idempotencyKey));
}
