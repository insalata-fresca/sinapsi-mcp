using System.Collections.Concurrent;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>In-memory <see cref="IRecentEnrollmentStore"/> — offline slice / tests. No I/O.</summary>
public sealed class InMemoryRecentEnrollmentStore : IRecentEnrollmentStore
{
    private readonly ConcurrentDictionary<string, string> _recent = new(StringComparer.Ordinal);

    public Task MarkAsync(string personSlug, string humanBasisId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug))
            throw new ArgumentException("personSlug must be non-empty", nameof(personSlug));
        if (string.IsNullOrWhiteSpace(humanBasisId))
            throw new ArgumentException("humanBasisId must be non-empty", nameof(humanBasisId));
        _recent[personSlug] = humanBasisId;
        return Task.CompletedTask;
    }

    public Task<string?> GetBasisAsync(string personSlug, CancellationToken ct = default) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(personSlug) && _recent.TryGetValue(personSlug, out var b) ? b : null);

    public Task ClearAsync(string personSlug, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(personSlug))
            _recent.TryRemove(personSlug, out _);
        return Task.CompletedTask;
    }
}
