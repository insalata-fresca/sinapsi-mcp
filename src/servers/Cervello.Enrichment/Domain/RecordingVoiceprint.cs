using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Domain;

/// <summary>
/// One durably-persisted per-recording, per-merged-cluster centroid row — the raw corpus material
/// M4's cross-recording resolver will cluster over (design doc <c>ste/cervello</c>
/// <c>docs/design/autonomous-attribution.md</c> §4.1/§5 M3; finding 3.a: the diarize sidecar already
/// emits per-recording per-speaker 192-d L2-normalised ECAPA centroids, but engine-side they were only
/// held transiently in <see cref="Cervello.Enrichment.DiarizedCentroidEnrollmentSourceProvider"/> and
/// evicted after open-point resolution — nothing accumulated).
///
/// <para><b>Keyed on centroid identity, never a label.</b> The key is
/// <c>(RecordingId, ClusterIndex)</c> — <see cref="ClusterIndex"/> is this recording's POSITION in
/// the deterministically-ordered <c>MergedCluster</c> list <see cref="Pipeline.ClusterMergeStage"/>
/// produces (see <see cref="Pipeline.ClusterMerge"/> — the list is sorted by
/// <c>MergedCluster.MergedSpeaker</c>, so the ordering, and therefore the index, is stable for a
/// given recording's diarization output). The diarizer's own per-recording speaker label
/// (<c>s1…</c>, <c>MergedCluster.MergedSpeaker</c>) is <b>NOT</b> the key: those labels are
/// per-recording only and are NOT stable across recordings (over-splits but never merges two
/// people — cosine cut 0.62) — linking across recordings MUST be done on centroids, never by
/// trusting labels across recordings. <see cref="MergedSpeakerLabel"/> is carried as descriptive
/// metadata only (useful for debugging/audit), never as part of the corpus identity.</para>
///
/// <para>Confinement (design §4.1, mirrors DESIGN §10.4 for the confirmed <c>voiceprints</c> table):
/// this is derived biometric data — CT146 pgvector ONLY, never git, never a shared subject, never
/// off-CT. The deletion runbook purges a person's contributing rows here too (an M4/M6 concern; this
/// mission persists the raw corpus, it does not implement the deletion cascade).</para>
/// </summary>
public sealed record RecordingVoiceprint
{
    public RecordingVoiceprint(
        string recordingId,
        int clusterIndex,
        IReadOnlyList<float> centroid,
        string model,
        int segmentCount,
        double durationSeconds,
        string mergedSpeakerLabel,
        DateTimeOffset createdAt,
        IReadOnlyList<DiarizedSegment>? segments = null)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("RecordingVoiceprint.RecordingId must be non-empty", nameof(recordingId));
        if (clusterIndex < 0)
            throw new ArgumentException("RecordingVoiceprint.ClusterIndex must be >= 0", nameof(clusterIndex));
        ArgumentNullException.ThrowIfNull(centroid);
        if (centroid.Count != SpeakerEmbedding.ExpectedDim)
            throw new ArgumentException(
                $"RecordingVoiceprint.Centroid must be {SpeakerEmbedding.ExpectedDim}-d (got {centroid.Count})",
                nameof(centroid));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("RecordingVoiceprint.Model must be non-empty", nameof(model));
        if (segmentCount < 0)
            throw new ArgumentException("RecordingVoiceprint.SegmentCount must be >= 0", nameof(segmentCount));
        if (durationSeconds < 0)
            throw new ArgumentException("RecordingVoiceprint.DurationSeconds must be >= 0", nameof(durationSeconds));
        if (string.IsNullOrWhiteSpace(mergedSpeakerLabel))
            throw new ArgumentException(
                "RecordingVoiceprint.MergedSpeakerLabel must be non-empty", nameof(mergedSpeakerLabel));

        RecordingId = recordingId;
        ClusterIndex = clusterIndex;
        Centroid = centroid;
        Model = model;
        SegmentCount = segmentCount;
        DurationSeconds = durationSeconds;
        MergedSpeakerLabel = mergedSpeakerLabel;
        CreatedAt = createdAt;
        Segments = segments ?? Array.Empty<DiarizedSegment>();
    }

    public string RecordingId { get; }

    /// <summary>
    /// This recording's positional index into the deterministically-ordered merged-cluster list
    /// (NOT the diarizer's <c>s1…</c> label). Part of the identity key together with
    /// <see cref="RecordingId"/>.
    /// </summary>
    public int ClusterIndex { get; }

    /// <summary>The 192-d L2-normalised ECAPA centroid for this merged cluster (biometric — CT146-only).</summary>
    public IReadOnlyList<float> Centroid { get; }

    /// <summary>The sidecar model id this centroid's vector space belongs to (space-compatibility gating).</summary>
    public string Model { get; }

    /// <summary>How many diarized segments were merged into this cluster (metadata, not identity).</summary>
    public int SegmentCount { get; }

    /// <summary>Total diarized duration (seconds) contributing to this cluster's centroid.</summary>
    public double DurationSeconds { get; }

    /// <summary>
    /// The diarizer-local merged-speaker label (<c>MergedCluster.MergedSpeaker</c>) at persist time —
    /// descriptive/audit metadata only. NEVER part of the corpus identity key (it is not stable
    /// across recordings).
    /// </summary>
    public string MergedSpeakerLabel { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// The per-segment <c>{speaker, start, end}</c> time-ranges (seconds) that contributed to this
    /// merged cluster — the same union <c>MergedCluster.Segments</c> carries in-flight (design
    /// <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §1.1/§5, V0). Persisted so a later
    /// phase (the naming surface's representative-segment pick + clip-cutter) can locate "which
    /// <c>{start,end}</c> of which recording is this voice" without re-running the diarizer. Metadata
    /// only — non-biometric (times, not vectors) — but CT146-side alongside the centroid, never git
    /// (design §5 confinement note). Never empty for a cluster produced by the live pipeline (a
    /// merged cluster always has &gt;= 1 source segment); MAY be empty for a hand-built/legacy row
    /// (pre-V0 persisted rows, or a test row that doesn't need ranges).
    /// </summary>
    public IReadOnlyList<DiarizedSegment> Segments { get; }
}
