using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Domain;

/// <summary>
/// The durable CT146-side link between a <c>unknown_NN.m4a</c> Drive sample and the voiceprint
/// centroid it was cut from (design <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7
/// phase V4, §4.4: <c>unknown_NN → {centroid, source (recording_id, cluster_index) rows,
/// drive_file_id}</c>, table <c>voiceprint_naming_candidates</c>).
///
/// <para><b>Why this exists.</b> <see cref="VoiceReviewCluster"/> (V1) is a throwaway snapshot —
/// recomputed fresh every call, never persisted as identity (design §3). Once V4 cuts a clip and
/// uploads it, SOMETHING must durably remember which centroid <c>unknown_03.m4a</c> came from, keyed
/// on the Drive file id (stable across a rename) — so V5's rename-poller can resolve "the operator
/// renamed this exact file" back to a centroid to enroll, without re-running the (non-deterministic
/// across corpus changes) V1 clustering. This row IS that memory.</para>
///
/// <para><b>Keyed on Drive file id, not filename.</b> <see cref="DriveFileId"/> is the resolution key
/// (§6.2: "filename volatility never matters — resolution is by Drive file id"); <see cref="SampleName"/>
/// (<c>unknown_NN</c>) is the name AT UPLOAD TIME, kept for operator-facing display/logging only.</para>
///
/// <para>Confinement: the centroid is CT146 pgvector-only (DESIGN §10.4) — never git, never Drive,
/// never a shared subject. Only the Drive file id (an opaque handle, not biometric material) and the
/// non-biometric source (recording_id, cluster_index) rows travel with this record.</para>
/// </summary>
public sealed record VoiceprintNamingCandidate
{
    public VoiceprintNamingCandidate(
        string sampleName,
        string driveFileId,
        IReadOnlyList<float> centroid,
        IReadOnlyList<VoiceReviewMember> sourceMembers,
        DateTimeOffset createdAt,
        bool resolved = false)
    {
        if (string.IsNullOrWhiteSpace(sampleName))
            throw new ArgumentException("VoiceprintNamingCandidate.SampleName must be non-empty", nameof(sampleName));
        if (string.IsNullOrWhiteSpace(driveFileId))
            throw new ArgumentException("VoiceprintNamingCandidate.DriveFileId must be non-empty", nameof(driveFileId));
        ArgumentNullException.ThrowIfNull(centroid);
        if (centroid.Count != SpeakerEmbedding.ExpectedDim)
            throw new ArgumentException(
                $"VoiceprintNamingCandidate.Centroid must be {SpeakerEmbedding.ExpectedDim}-d (got {centroid.Count})",
                nameof(centroid));
        ArgumentNullException.ThrowIfNull(sourceMembers);
        if (sourceMembers.Count == 0)
            throw new ArgumentException(
                "VoiceprintNamingCandidate must have >= 1 source member", nameof(sourceMembers));

        SampleName = sampleName;
        DriveFileId = driveFileId;
        Centroid = centroid;
        SourceMembers = sourceMembers;
        CreatedAt = createdAt;
        Resolved = resolved;
    }

    /// <summary>The upload-time sample name (<c>unknown_NN</c>) — display/logging only, NOT the resolution key.</summary>
    public string SampleName { get; }

    /// <summary>The Drive file id — stable across a rename; the resolution key V5's poller looks up by.</summary>
    public string DriveFileId { get; }

    /// <summary>The 256-d merged centroid this sample was cut from (CT146-only; never leaves this store).</summary>
    public IReadOnlyList<float> Centroid { get; }

    /// <summary>Every contributing <c>(recordingId, clusterIndex)</c> row this voice's centroid was merged from.</summary>
    public IReadOnlyList<VoiceReviewMember> SourceMembers { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// True once V5's rename-poller has resolved this candidate to an enrollment (renamed + enrolled).
    /// A future generate-samples run clears only UN-resolved candidates + their Drive files (§5:
    /// "re-running regenerates the candidate set") — a resolved row is the operator's durable naming
    /// decision and is never silently swept.
    /// </summary>
    public bool Resolved { get; init; }
}
