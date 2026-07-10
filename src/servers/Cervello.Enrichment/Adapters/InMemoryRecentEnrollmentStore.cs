using System.Collections.Concurrent;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// In-memory <see cref="IRecentEnrollmentStore"/> — offline slice / tests. No I/O. Enforces the SAME
/// TTL bound as <see cref="PgRecentEnrollmentStore"/>: <see cref="GetBasisAsync"/> returns null for a
/// mark older than <see cref="_ttl"/>, so a stale mark can never re-authorise an auto-apply. The clock
/// is injectable so a test can simulate the TTL elapsing without wall-clock sleeps.
/// </summary>
public sealed class InMemoryRecentEnrollmentStore : IRecentEnrollmentStore
{
    private readonly record struct Mark(string BasisId, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, Mark> _recent = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Default: a 3-hour TTL against the system clock (mirrors the config default).</summary>
    public InMemoryRecentEnrollmentStore() : this(TimeSpan.FromMinutes(180), () => DateTimeOffset.UtcNow) { }

    /// <summary>Explicit TTL + clock (tests inject a mutable clock to simulate the window elapsing).</summary>
    public InMemoryRecentEnrollmentStore(TimeSpan ttl, Func<DateTimeOffset> now)
    {
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive");
        _ttl = ttl;
        _now = now ?? throw new ArgumentNullException(nameof(now));
    }

    public Task MarkAsync(string personSlug, string humanBasisId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug))
            throw new ArgumentException("personSlug must be non-empty", nameof(personSlug));
        if (string.IsNullOrWhiteSpace(humanBasisId))
            throw new ArgumentException("humanBasisId must be non-empty", nameof(humanBasisId));
        _recent[personSlug] = new Mark(humanBasisId, _now());
        return Task.CompletedTask;
    }

    public Task<string?> GetBasisAsync(string personSlug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug) || !_recent.TryGetValue(personSlug, out var mark))
            return Task.FromResult<string?>(null);
        // Write-safety bound: a mark older than the TTL is INERT — it must not authorise an auto-apply.
        if (_now() - mark.At > _ttl)
            return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(mark.BasisId);
    }
}
