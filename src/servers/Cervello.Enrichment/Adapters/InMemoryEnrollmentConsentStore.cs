using System.Collections.Concurrent;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>In-memory <see cref="IEnrollmentConsentStore"/> — offline slice / tests. No I/O.</summary>
public sealed class InMemoryEnrollmentConsentStore : IEnrollmentConsentStore
{
    private readonly ConcurrentDictionary<string, string> _consented = new(StringComparer.Ordinal);

    public Task<bool> AddConsentAsync(string personSlug, string basisId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(personSlug))
            throw new ArgumentException("personSlug must be non-empty", nameof(personSlug));
        var added = _consented.TryAdd(personSlug, basisId ?? "");
        return Task.FromResult(added);
    }

    public Task<bool> IsConsentedAsync(string personSlug, CancellationToken ct = default) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(personSlug) && _consented.ContainsKey(personSlug));
}
