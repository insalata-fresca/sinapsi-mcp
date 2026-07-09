using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// In-memory <see cref="IRecordingVoiceprintStore"/> for tests + the offline slice. Mirrors the
/// CT146 pgvector <see cref="PgRecordingVoiceprintStore"/> contract without a database: upsert by
/// <c>(recordingId, clusterIndex)</c>, per-recording fetch, corpus-wide fetch.
///
/// <para>Confinement (design §4.1, mirrors <see cref="InMemoryVoiceprintStore"/>): a synthetic
/// vector is all that ever enters this store in tests; no personal audio, no real biometric vector
/// is committed to git.</para>
/// </summary>
public sealed class InMemoryRecordingVoiceprintStore : IRecordingVoiceprintStore
{
    // Keyed by (recordingId, clusterIndex) — never the diarizer's per-recording label.
    private readonly Dictionary<(string RecordingId, int ClusterIndex), RecordingVoiceprint> _rows = new();

    public Task PersistAsync(
        string recordingId, IReadOnlyList<RecordingVoiceprint> clusters, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));
        ArgumentNullException.ThrowIfNull(clusters);

        foreach (var row in clusters)
        {
            if (!string.Equals(row.RecordingId, recordingId, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"cluster row RecordingId '{row.RecordingId}' does not match '{recordingId}'", nameof(clusters));
            // Upsert by (recordingId, clusterIndex) — idempotent on re-processing the same recording.
            _rows[(row.RecordingId, row.ClusterIndex)] = row;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RecordingVoiceprint>> GetForRecordingAsync(
        string recordingId, CancellationToken ct = default)
    {
        var rows = _rows.Values
            .Where(r => string.Equals(r.RecordingId, recordingId, StringComparison.Ordinal))
            .OrderBy(r => r.ClusterIndex)
            .ToList();
        return Task.FromResult<IReadOnlyList<RecordingVoiceprint>>(rows);
    }

    public Task<IReadOnlyList<RecordingVoiceprint>> GetCorpusAsync(CancellationToken ct = default)
    {
        var rows = _rows.Values
            .OrderBy(r => r.RecordingId, StringComparer.Ordinal)
            .ThenBy(r => r.ClusterIndex)
            .ToList();
        return Task.FromResult<IReadOnlyList<RecordingVoiceprint>>(rows);
    }
}
