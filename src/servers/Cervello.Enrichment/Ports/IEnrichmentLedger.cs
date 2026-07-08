namespace Cervello.Enrichment.Ports;

/// <summary>
/// Idempotency ledger for the enrichment pipeline (SCHEMAS §5): records which
/// <c>rec:&lt;recordingId&gt;:&lt;audio-sha256&gt;</c> keys have already been picked up so a
/// replay of a seen key is a logged no-op. A fake / in-memory implementation is used in tests.
/// </summary>
public interface IEnrichmentLedger
{
    /// <summary>
    /// Atomically claim an idempotency key. Returns <c>true</c> if this call claimed it
    /// (first time — proceed), <c>false</c> if it was already claimed (replay — no-op).
    /// </summary>
    Task<bool> TryClaimAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>Whether a key has been claimed already.</summary>
    Task<bool> IsClaimedAsync(string idempotencyKey, CancellationToken ct = default);
}
