using Cervello.Enrichment.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline.Stages;

/// <summary>
/// Collapses over-split diarization clusters into merged attribution units by centroid cosine
/// similarity (spec <c>speaker-attribution</c> → "Merge over-split diarization clusters"). Thin
/// stage wrapper over <see cref="Pipeline.ClusterMerge"/>: cutoff = the review-band floor
/// (0.62). One real person yields one <see cref="MergedCluster"/>; distinct speakers are never
/// merged. The merged clusters are the unit the (E3) attribution stage matches against enrolled
/// voiceprints — raw diarizer cluster ids are never trusted (E0.5 §5).
/// </summary>
public sealed class ClusterMergeStage(
    double mergeCutoff = ClusterMerge.DefaultMergeCutoff,
    ILogger<ClusterMergeStage>? logger = null)
{
    private readonly double _cutoff = mergeCutoff;
    private readonly ILogger _log = logger ?? NullLogger<ClusterMergeStage>.Instance;

    public IReadOnlyList<MergedCluster> Merge(IReadOnlyList<SpeakerCluster> clusters)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        var merged = ClusterMerge.Merge(clusters, _cutoff);
        _log.LogInformation("cluster-merge: {In} local clusters → {Out} merged units (cutoff {Cutoff})",
            clusters.Count, merged.Count, _cutoff);
        return merged;
    }
}
