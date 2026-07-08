using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Math;

namespace Cervello.Enrichment.Pipeline;

/// <summary>
/// Merges over-split diarization clusters by centroid cosine similarity — NOT by trusting raw
/// cluster ids (E0.5 §4/§5; spec <c>speaker-attribution</c> → "Merge over-split diarization
/// clusters"). Two local clusters with centroid cosine ≥ the merge cutoff (the review-band
/// floor, <see cref="DefaultMergeCutoff"/> = 0.62) are the same speaker; below the cutoff they
/// stay separate. Merging is transitive (single-linkage union-find at the cutoff): one real
/// person collapses to one <see cref="MergedCluster"/>, and two distinct embeddings are never
/// merged. The merged centroid is recomputed over all members' centroids.
/// </summary>
public static class ClusterMerge
{
    /// <summary>The review-band floor used as the same-speaker merge cutoff (E0.5 §2/§3: 0.62).</summary>
    public const double DefaultMergeCutoff = 0.62;

    public static IReadOnlyList<MergedCluster> Merge(
        IReadOnlyList<SpeakerCluster> clusters,
        double mergeCutoff = DefaultMergeCutoff)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        var n = clusters.Count;
        if (n == 0) return Array.Empty<MergedCluster>();

        // Union-find over cluster indices; union any pair with centroid cosine >= cutoff.
        var parent = new int[n];
        for (var i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[System.Math.Max(ra, rb)] = System.Math.Min(ra, rb);
        }

        for (var i = 0; i < n; i++)
        for (var j = i + 1; j < n; j++)
        {
            var sim = Cosine.Similarity(clusters[i].Centroid, clusters[j].Centroid);
            if (sim >= mergeCutoff) Union(i, j);
        }

        // Group by root, then build one MergedCluster per group.
        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < n; i++)
        {
            var r = Find(i);
            if (!groups.TryGetValue(r, out var list)) groups[r] = list = [];
            list.Add(i);
        }

        var merged = new List<MergedCluster>(groups.Count);
        foreach (var group in groups.Values)
        {
            var members = group.Select(i => clusters[i]).ToList();
            var memberLabels = members.Select(m => m.Speaker).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var centroids = members.Select(m => m.Centroid).ToList();
            var mergedCentroid = Cosine.Centroid(centroids);
            var segments = members
                .SelectMany(m => m.Segments)
                .OrderBy(s => s.Start)
                .ToList();

            merged.Add(new MergedCluster(
                mergedSpeaker: memberLabels[0], // deterministic label
                memberSpeakers: memberLabels,
                centroid: mergedCentroid,
                segments: segments));
        }

        // Deterministic ordering of the merged units by their label.
        return merged.OrderBy(m => m.MergedSpeaker, StringComparer.Ordinal).ToList();
    }
}
