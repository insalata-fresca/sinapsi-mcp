using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// In-memory <see cref="IOpenPointStore"/> for tests (mirrors the CT146 <c>open_points</c> table's
/// contract). Idempotent on point id so a re-run of apply is a no-op; resolution is single-shot so
/// an answered point cannot be double-applied. Never git.
/// </summary>
public sealed class InMemoryOpenPointStore : IOpenPointStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, OpenPoint> _points = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenPointResolution> _resolved = new(StringComparer.Ordinal);

    public Task<bool> EnqueueAsync(OpenPoint point, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(point);
        lock (_lock)
        {
            if (_points.ContainsKey(point.PointId)) return Task.FromResult(false);
            _points[point.PointId] = point;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<OpenPoint>> ListPendingAsync(string? recordingId = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<OpenPoint> result = _points.Values
                .Where(p => !_resolved.ContainsKey(p.PointId))
                .Where(p => recordingId is null || MatchesRecording(p, recordingId))
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<OpenPoint?> GetAsync(string pointId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _points.TryGetValue(pointId, out var p);
            return Task.FromResult(p);
        }
    }

    public Task<bool> ResolveAsync(string pointId, OpenPointResolution resolution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        lock (_lock)
        {
            if (!_points.ContainsKey(pointId)) return Task.FromResult(false);
            if (_resolved.ContainsKey(pointId)) return Task.FromResult(false); // already resolved — double-apply guard
            _resolved[pointId] = resolution;
            return Task.FromResult(true);
        }
    }

    public Task<bool> IsResolvedAsync(string pointId, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_resolved.ContainsKey(pointId));
    }

    private static bool MatchesRecording(OpenPoint p, string recordingId)
    {
        // Accept either the bare id or a rec:// form on either side.
        var a = p.RecordingId.StartsWith("rec://", StringComparison.Ordinal) ? p.RecordingId["rec://".Length..] : p.RecordingId;
        var b = recordingId.StartsWith("rec://", StringComparison.Ordinal) ? recordingId["rec://".Length..] : recordingId;
        return string.Equals(a, b, StringComparison.Ordinal);
    }
}
