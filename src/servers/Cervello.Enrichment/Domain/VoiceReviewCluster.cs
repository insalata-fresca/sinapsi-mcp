using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Domain;

/// <summary>
/// One member row contributing to a <see cref="VoiceReviewCluster"/> — a single persisted
/// <c>recording_voiceprints</c> centroid (design doc <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V1). Carries only the identity key
/// (<c>RecordingId</c>, <c>ClusterIndex</c>) plus the metadata the operator review needs
/// (duration, segment count) — never the centroid itself (that stays on the parent cluster).
/// </summary>
public sealed record VoiceReviewMember
{
    public VoiceReviewMember(string recordingId, int clusterIndex, double durationSeconds, int segmentCount)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("VoiceReviewMember.RecordingId must be non-empty", nameof(recordingId));
        if (clusterIndex < 0)
            throw new ArgumentException("VoiceReviewMember.ClusterIndex must be >= 0", nameof(clusterIndex));
        if (durationSeconds < 0)
            throw new ArgumentException("VoiceReviewMember.DurationSeconds must be >= 0", nameof(durationSeconds));
        if (segmentCount < 0)
            throw new ArgumentException("VoiceReviewMember.SegmentCount must be >= 0", nameof(segmentCount));

        RecordingId = recordingId;
        ClusterIndex = clusterIndex;
        DurationSeconds = durationSeconds;
        SegmentCount = segmentCount;
    }

    /// <summary>The recording this centroid came from — part of the corpus identity key.</summary>
    public string RecordingId { get; }

    /// <summary>The recording-local merged-cluster position — part of the corpus identity key.</summary>
    public int ClusterIndex { get; }

    /// <summary>This member's persisted diarized duration (seconds) — feeds coverage stats.</summary>
    public double DurationSeconds { get; }

    /// <summary>This member's persisted segment count — feeds coverage stats.</summary>
    public int SegmentCount { get; }
}

/// <summary>
/// One distinct candidate voice in a one-shot, read-only REVIEW SNAPSHOT of the voiceprint corpus
/// (design doc <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7 phase V1, §3 "the
/// one-shot review clustering"). Deliberately NOT the descoped M4 persistent cross-recording
/// identity resolver (<c>docs/design/autonomous-attribution.md</c> §0): this is thrown away and
/// recomputed fresh every time the operator asks — it never persists cluster identity, never
/// mutates on new recordings, and any non-determinism is bounded to a single review pass the
/// operator immediately acts on.
///
/// <para><b>Local id is NOT stable across runs.</b> <see cref="LocalId"/> (<c>voice_00</c>,
/// <c>voice_01</c>, …) only identifies this voice WITHIN one <see cref="VoiceReviewClusterer"/>
/// call, ordered by coverage (§7 V1: "ranks voices by coverage … so the top ~15 are the
/// enrollment candidates"). Re-running the clusterer after the corpus changes may reassign ids.</para>
/// </summary>
public sealed record VoiceReviewCluster
{
    public VoiceReviewCluster(
        string localId,
        IReadOnlyList<float> representativeCentroid,
        IReadOnlyList<VoiceReviewMember> members)
    {
        if (string.IsNullOrWhiteSpace(localId))
            throw new ArgumentException("VoiceReviewCluster.LocalId must be non-empty", nameof(localId));
        ArgumentNullException.ThrowIfNull(representativeCentroid);
        if (representativeCentroid.Count != SpeakerEmbedding.ExpectedDim)
            throw new ArgumentException(
                $"VoiceReviewCluster.RepresentativeCentroid must be {SpeakerEmbedding.ExpectedDim}-d " +
                $"(got {representativeCentroid.Count})",
                nameof(representativeCentroid));
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0)
            throw new ArgumentException("VoiceReviewCluster must have >= 1 member", nameof(members));

        LocalId = localId;
        RepresentativeCentroid = representativeCentroid;
        Members = members;
        RecordingCount = members.Select(m => m.RecordingId).Distinct(StringComparer.Ordinal).Count();
        SegmentCount = members.Sum(m => m.SegmentCount);
        TotalDurationSeconds = members.Sum(m => m.DurationSeconds);
    }

    /// <summary>
    /// Stable ONLY within this one clustering run (never across runs/corpus changes) — see the
    /// type doc. Assigned in coverage-descending order: <c>voice_00</c> is the most-covered voice.
    /// </summary>
    public string LocalId { get; }

    /// <summary>
    /// The merged centroid over every contributing member — the mean of all
    /// <c>recording_voiceprints</c> centroids grouped into this voice (CT146-only; never leaves
    /// the enrichment engine's process boundary via this type — no audio, no filenames).
    /// </summary>
    public IReadOnlyList<float> RepresentativeCentroid { get; }

    /// <summary>Every contributing <c>(recordingId, clusterIndex)</c> row, in stable input order.</summary>
    public IReadOnlyList<VoiceReviewMember> Members { get; }

    /// <summary>Distinct recordings this voice appears in — the operator's "how often" signal.</summary>
    public int RecordingCount { get; }

    /// <summary>Total diarized segment count summed across all members.</summary>
    public int SegmentCount { get; }

    /// <summary>Total diarized duration (seconds) summed across all members — the ranking key.</summary>
    public double TotalDurationSeconds { get; }
}
