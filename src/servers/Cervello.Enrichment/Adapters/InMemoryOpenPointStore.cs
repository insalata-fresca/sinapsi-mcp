using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// In-memory <see cref="IOpenPointStore"/> for tests (mirrors the CT146 <c>open_points</c> table's
/// contract). Idempotent on point id so a re-run of apply is a no-op. Never git.
/// </summary>
public sealed class InMemoryOpenPointStore : IOpenPointStore
{
    private readonly Dictionary<string, OpenPoint> _points = new(StringComparer.Ordinal);

    public Task<bool> EnqueueAsync(OpenPoint point, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (_points.ContainsKey(point.PointId)) return Task.FromResult(false);
        _points[point.PointId] = point;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<OpenPoint>> ListPendingAsync(string? recordingId = null, CancellationToken ct = default)
    {
        IReadOnlyList<OpenPoint> result = _points.Values
            .Where(p => recordingId is null || string.Equals(p.RecordingId, recordingId, StringComparison.Ordinal))
            .ToList();
        return Task.FromResult(result);
    }
}
