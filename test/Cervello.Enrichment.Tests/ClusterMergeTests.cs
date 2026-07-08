using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Math;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// speaker-attribution — "Merge over-split diarization clusters". Proves cluster-merge collapses
/// an over-split single speaker (high centroid cosine) and NEVER merges two distinct speakers
/// (low centroid cosine). Synthetic vectors only — no personal audio, no real biometrics.
/// </summary>
public sealed class ClusterMergeTests
{
    private static DiarizedSegment Seg(string spk, double start, double end) => new(spk, start, end);

    private static SpeakerCluster Cluster(string speaker, IReadOnlyList<float> centroid) =>
        new(speaker, centroid, new[] { Seg(speaker, 0.0, 1.0) });

    // ---- Scenario: Two over-split clusters of one speaker merge (centroid cosine 0.88) ----
    [Fact]
    public void Two_over_split_clusters_of_one_speaker_merge()
    {
        // s1 = pure axis-0; s3 = tilted so cos(s1, s3) == 0.88 (same speaker, over-split).
        var s1 = Cluster("s1", TestVectors.Axis(0));
        var s3 = Cluster("s3", TestVectors.TiltedFromAxis(0, 5, 0.88));

        Assert.Equal(0.88, Cosine.Similarity(s1.Centroid, s3.Centroid), 3);

        var merged = ClusterMerge.Merge(new[] { s1, s3 });

        Assert.Single(merged);
        Assert.Equal(new[] { "s1", "s3" }, merged[0].MemberSpeakers);
        Assert.Equal("s1", merged[0].MergedSpeaker); // deterministic: lexicographically first
    }

    // ---- Scenario: Distinct speakers are not merged (centroid cosine 0.33) ----
    [Fact]
    public void Distinct_speakers_are_not_merged()
    {
        var a = Cluster("s1", TestVectors.Axis(0));
        var b = Cluster("s2", TestVectors.TiltedFromAxis(0, 10, 0.33));

        Assert.Equal(0.33, Cosine.Similarity(a.Centroid, b.Centroid), 3);

        var merged = ClusterMerge.Merge(new[] { a, b });

        Assert.Equal(2, merged.Count);
        Assert.All(merged, m => Assert.Single(m.MemberSpeakers));
    }

    // ---- The mission fixture: over-split single speaker collapses AND two distinct never merge ----
    [Fact]
    public void Over_split_single_speaker_collapses_while_a_distinct_speaker_stays_separate()
    {
        // Three local clusters of speaker A (all mutually >= 0.62) + one cluster of speaker B (far).
        var a1 = Cluster("s1", TestVectors.Axis(0));
        var a2 = Cluster("s2", TestVectors.TiltedFromAxis(0, 20, 0.90));
        var a3 = Cluster("s4", TestVectors.TiltedFromAxis(0, 21, 0.80));
        var b1 = Cluster("s3", TestVectors.TiltedFromAxis(0, 22, 0.20)); // distinct person

        var merged = ClusterMerge.Merge(new[] { a1, a2, a3, b1 });

        // Exactly two merged units: {s1,s2,s4} (person A) and {s3} (person B).
        Assert.Equal(2, merged.Count);
        var personA = merged.Single(m => m.MemberSpeakers.Contains("s1"));
        Assert.Equal(new[] { "s1", "s2", "s4" }, personA.MemberSpeakers);
        var personB = merged.Single(m => m.MemberSpeakers.Contains("s3"));
        Assert.Equal(new[] { "s3" }, personB.MemberSpeakers);
        // B's centroid never got averaged into A's unit.
        Assert.DoesNotContain("s3", personA.MemberSpeakers);
    }

    // ---- Transitivity: A~B and B~C (both >= cutoff) but A~C < cutoff still single-links into one ----
    [Fact]
    public void Single_linkage_is_transitive_at_the_cutoff()
    {
        // Build a chain where each adjacent pair is >= 0.62 but the ends are < 0.62.
        // Walk along the e0→e1 arc: a at angle 0°, b at 45°, c at 90°.
        //   cos(a,b) = cos(a,c-arc midpoint)… concretely cos(45°)=.707 for both adjacent
        //   pairs, and cos(a,c) = cos(90°) = 0 for the ends.
        var a = Cluster("s1", TestVectors.Axis(0));                       // 0° on the e0/e1 plane
        var b = Cluster("s2", TestVectors.TiltedFromAxis(0, 1, 0.7071));  // 45°: cos(a,b)=.707
        var c = Cluster("s3", TestVectors.Axis(1));                       // 90°: cos(b,c)=.707, cos(a,c)=0

        var abClose = Cosine.Similarity(a.Centroid, b.Centroid) >= 0.62;
        var bcClose = Cosine.Similarity(b.Centroid, c.Centroid) >= 0.62;
        var acFar = Cosine.Similarity(a.Centroid, c.Centroid) < 0.62;
        Assert.True(abClose && bcClose && acFar,
            $"expected a chain: ab>=.62 bc>=.62 ac<.62 (got ab={Cosine.Similarity(a.Centroid, b.Centroid):F2} " +
            $"bc={Cosine.Similarity(b.Centroid, c.Centroid):F2} ac={Cosine.Similarity(a.Centroid, c.Centroid):F2})");

        var merged = ClusterMerge.Merge(new[] { a, b, c });
        Assert.Single(merged); // transitively one unit
        Assert.Equal(new[] { "s1", "s2", "s3" }, merged[0].MemberSpeakers);
    }

    [Fact]
    public void Empty_input_yields_no_merged_clusters()
    {
        Assert.Empty(ClusterMerge.Merge(Array.Empty<SpeakerCluster>()));
    }

    [Fact]
    public void Merged_unit_unions_all_member_segments_ordered_by_start()
    {
        var a1 = new SpeakerCluster("s1", TestVectors.Axis(0),
            new[] { new DiarizedSegment("s1", 5.0, 6.0) });
        var a2 = new SpeakerCluster("s2", TestVectors.TiltedFromAxis(0, 3, 0.90),
            new[] { new DiarizedSegment("s2", 1.0, 2.0) });

        var merged = ClusterMerge.Merge(new[] { a1, a2 });
        Assert.Single(merged);
        Assert.Equal(new[] { 1.0, 5.0 }, merged[0].Segments.Select(s => s.Start).ToArray());
    }

    // ---- The stage wrapper produces the same result and uses the 0.62 default cutoff ----
    [Fact]
    public void ClusterMergeStage_uses_the_review_band_floor_by_default()
    {
        Assert.Equal(0.62, ClusterMerge.DefaultMergeCutoff);
        var stage = new ClusterMergeStage();
        var s1 = Cluster("s1", TestVectors.Axis(0));
        var s3 = Cluster("s3", TestVectors.TiltedFromAxis(0, 5, 0.88));
        Assert.Single(stage.Merge(new[] { s1, s3 }));
    }

    [Fact]
    public void Cosine_similarity_of_a_zero_vector_is_zero_not_nan()
    {
        var zero = new float[SpeakerEmbedding.ExpectedDim];
        Assert.Equal(0.0, Cosine.Similarity(zero, TestVectors.Axis(0)));
    }
}
