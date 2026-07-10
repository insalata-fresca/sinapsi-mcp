using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for the durable per-recording voiceprint CORPUS store (design doc <c>ste/cervello</c>
/// <c>docs/design/autonomous-attribution.md</c> §4.1/§5 M3) — the raw material M4's cross-recording
/// resolver clusters over. Distinct from <see cref="IVoiceprintStore"/>: that store holds ONE row per
/// enrolled, §10-allowlisted person (the confirmed identity store); THIS store holds EVERY
/// recording's per-speaker merged-cluster centroids, confirmed or not — nothing is evicted.
///
/// <para><b>Keyed on centroid identity, never a label.</b> Every operation keys on
/// <c>(recordingId, clusterIndex)</c> — the recording's merged-cluster POSITION, never the
/// diarizer's per-recording <c>s1…</c> label (those labels are not stable across recordings; see
/// <see cref="RecordingVoiceprint"/> for the full rationale). Persisting the same recording's
/// clusters again (a re-processed recording) UPSERTS by that key — it never duplicates rows.</para>
///
/// <para>Confinement: like <see cref="IVoiceprintStore"/>, this is derived biometric data — CT146
/// pgvector ONLY, never git, never a shared subject, never off-CT (DESIGN §10.4 posture, extended by
/// the M3 design amendment to <c>recording_voiceprints</c>).</para>
/// </summary>
public interface IRecordingVoiceprintStore
{
    /// <summary>
    /// Durably persist (upsert) every merged cluster's centroid for one recording, keyed on
    /// <c>(recordingId, clusterIndex)</c> — the list POSITION, never <c>MergedCluster.MergedSpeaker</c>.
    /// Idempotent: re-persisting the same recording (a replay/re-process) upserts existing rows,
    /// never duplicates. An empty list is a legal no-op (a recording with zero merged clusters).
    /// </summary>
    Task PersistAsync(string recordingId, IReadOnlyList<RecordingVoiceprint> clusters, CancellationToken ct = default);

    /// <summary>Fetch all persisted centroid rows for one recording, ordered by <c>ClusterIndex</c>.</summary>
    Task<IReadOnlyList<RecordingVoiceprint>> GetForRecordingAsync(string recordingId, CancellationToken ct = default);

    /// <summary>
    /// Fetch every persisted row across the WHOLE corpus (every recording) — the raw material M4's
    /// cross-recording clusterer reads. Ordered by <c>(RecordingId, ClusterIndex)</c> for determinism.
    /// </summary>
    Task<IReadOnlyList<RecordingVoiceprint>> GetCorpusAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetch the persisted segment time-ranges for ONE cluster, keyed on <c>(recordingId,
    /// clusterIndex)</c> — the same identity key as everything else on this port (design
    /// <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §1.1/§5, V0). This is what the
    /// naming surface's representative-segment pick + clip-cutter (a later phase) calls to locate
    /// "which <c>{start,end}</c> of which recording is this voice". Ordered by <c>Start</c> ascending.
    /// Returns an empty list for an unknown/absent cluster or a pre-V0 row with no persisted ranges —
    /// never throws for a missing key.
    /// </summary>
    Task<IReadOnlyList<DiarizedSegment>> GetSegmentsAsync(
        string recordingId, int clusterIndex, CancellationToken ct = default);
}
