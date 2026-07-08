using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Domain;

/// <summary>
/// A single diarizer-local speaker cluster: the sidecar's <c>speaker</c> label, its centroid
/// embedding, and the diarized segments attributed to it. This is the raw (over-split-prone)
/// diarizer output — one real person may appear as several of these (E0.5 §4).
/// </summary>
public sealed record SpeakerCluster
{
    public SpeakerCluster(string speaker, IReadOnlyList<float> centroid, IReadOnlyList<DiarizedSegment> segments)
    {
        if (string.IsNullOrWhiteSpace(speaker))
            throw new ArgumentException("SpeakerCluster.Speaker must be non-empty", nameof(speaker));
        ArgumentNullException.ThrowIfNull(centroid);
        ArgumentNullException.ThrowIfNull(segments);
        if (centroid.Count != SpeakerEmbedding.ExpectedDim)
            throw new ArgumentException(
                $"SpeakerCluster.Centroid must be {SpeakerEmbedding.ExpectedDim}-d (got {centroid.Count})",
                nameof(centroid));
        Speaker = speaker;
        Centroid = centroid;
        Segments = segments;
    }

    public string Speaker { get; }
    public IReadOnlyList<float> Centroid { get; }
    public IReadOnlyList<DiarizedSegment> Segments { get; }
}

/// <summary>
/// A merged speaker cluster — the attribution unit (spec <c>speaker-attribution</c> →
/// "Merge over-split diarization clusters"). One real person = one <see cref="MergedCluster"/>,
/// even when the diarizer over-split them into several <see cref="SpeakerCluster"/>s. Carries
/// the recomputed centroid over all merged members and the union of their segments.
/// </summary>
public sealed record MergedCluster
{
    public MergedCluster(
        string mergedSpeaker,
        IReadOnlyList<string> memberSpeakers,
        IReadOnlyList<float> centroid,
        IReadOnlyList<DiarizedSegment> segments)
    {
        if (string.IsNullOrWhiteSpace(mergedSpeaker))
            throw new ArgumentException("MergedCluster.MergedSpeaker must be non-empty", nameof(mergedSpeaker));
        ArgumentNullException.ThrowIfNull(memberSpeakers);
        if (memberSpeakers.Count == 0)
            throw new ArgumentException("MergedCluster must have >= 1 member speaker", nameof(memberSpeakers));
        ArgumentNullException.ThrowIfNull(centroid);
        ArgumentNullException.ThrowIfNull(segments);
        if (centroid.Count != SpeakerEmbedding.ExpectedDim)
            throw new ArgumentException(
                $"MergedCluster.Centroid must be {SpeakerEmbedding.ExpectedDim}-d (got {centroid.Count})",
                nameof(centroid));
        MergedSpeaker = mergedSpeaker;
        MemberSpeakers = memberSpeakers;
        Centroid = centroid;
        Segments = segments;
    }

    /// <summary>The label for the merged unit (the lexicographically-first member for determinism).</summary>
    public string MergedSpeaker { get; }

    /// <summary>The diarizer-local speaker labels that were merged into this unit.</summary>
    public IReadOnlyList<string> MemberSpeakers { get; }

    /// <summary>Centroid recomputed over all merged members (the match target for attribution).</summary>
    public IReadOnlyList<float> Centroid { get; }

    /// <summary>Union of all merged members' segments.</summary>
    public IReadOnlyList<DiarizedSegment> Segments { get; }
}
