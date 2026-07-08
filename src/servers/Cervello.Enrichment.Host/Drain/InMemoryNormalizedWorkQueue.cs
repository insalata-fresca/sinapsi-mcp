using System.Collections.Concurrent;
using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Host.Drain;

/// <summary>
/// In-memory drain source (tests + the offline slice). Holds a set of recordings and their current
/// state; <see cref="LeaseNormalizedAsync"/> returns those still in <c>normalized</c>, and
/// <see cref="AdvanceStateAsync"/> updates the tracked state so a subsequent lease no longer returns
/// an advanced recording — mirroring the live table's "only rows still at normalized" filter. This
/// keeps the drain WORKER testable without a database while enforcing the same drain contract.
/// </summary>
public sealed class InMemoryNormalizedWorkQueue : INormalizedWorkQueue
{
    private readonly ConcurrentDictionary<string, (RecordingRef Rec, EnrichmentState State)> _rows = new(StringComparer.Ordinal);

    /// <summary>Seed a recording at <c>normalized</c> (the Watcher's terminal write).</summary>
    public void SeedNormalized(RecordingRef recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        _rows[recording.IdempotencyKey] = (recording, EnrichmentState.Normalized);
    }

    /// <summary>The current tracked state of a recording (test assertion helper).</summary>
    public EnrichmentState StateOf(RecordingRef recording) => _rows[recording.IdempotencyKey].State;

    public Task<IReadOnlyList<RecordingRef>> LeaseNormalizedAsync(int max, CancellationToken ct = default)
    {
        if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max), "max must be positive");
        IReadOnlyList<RecordingRef> batch = _rows.Values
            .Where(r => r.State == EnrichmentState.Normalized)
            .Select(r => r.Rec)
            .Take(max)
            .ToList();
        return Task.FromResult(batch);
    }

    public Task AdvanceStateAsync(RecordingRef recording, EnrichmentState state, string? reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        _rows.AddOrUpdate(
            recording.IdempotencyKey,
            (recording, state),
            (_, existing) => (existing.Rec, state));
        return Task.CompletedTask;
    }
}
