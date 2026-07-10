using System.Collections.Concurrent;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// In-memory <see cref="IRecordingRequeue"/> — offline slice / tests. Records which recordings were
/// requeued (so a test can assert ONLY the matching ones were reset). By default every recording id is
/// "known" (requeue returns true); pass an explicit known-set to make an unknown id return false.
/// </summary>
public sealed class InMemoryRecordingRequeue : IRecordingRequeue
{
    private readonly HashSet<string>? _known;
    public ConcurrentBag<string> Requeued { get; } = [];

    /// <summary>All recording ids are known (requeue always succeeds).</summary>
    public InMemoryRecordingRequeue() => _known = null;

    /// <summary>Only <paramref name="knownRecordingIds"/> are known; an unknown id returns false.</summary>
    public InMemoryRecordingRequeue(IEnumerable<string> knownRecordingIds) =>
        _known = new HashSet<string>(knownRecordingIds, StringComparer.Ordinal);

    public Task<bool> RequeueForReattributionAsync(string recordingId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));
        var known = _known is null || _known.Contains(recordingId);
        if (known)
            Requeued.Add(recordingId);
        return Task.FromResult(known);
    }
}
