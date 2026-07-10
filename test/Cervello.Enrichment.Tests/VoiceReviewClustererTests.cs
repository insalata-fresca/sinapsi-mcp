using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Math;
using Cervello.Enrichment.Pipeline;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Voiceprint-naming V1 — the one-shot, READ-ONLY review clustering (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V1, §3). Proves: same voice across MULTIPLE
/// recordings clusters together; distinct voices stay separate; coverage stats (recording count,
/// segment count, total duration) are correct; ranking is coverage-descending; the snapshot is
/// deterministic for a fixed input + cutoff; already-enrolled voices are excluded when an enrolled
/// store is supplied; and nothing is persisted (repeated calls never mutate the corpus store).
///
/// <para>SYNTHETIC 256-d vectors only — no personal audio, no real biometric vector ever appears here.</para>
/// </summary>
public sealed class VoiceReviewClustererTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

    private static RecordingVoiceprint Row(
        string recordingId, int clusterIndex, float[] centroid,
        int segmentCount = 3, double duration = 12.5, string label = "s1") =>
        new(recordingId, clusterIndex, centroid, "pyannote/wespeaker-voxceleb-resnet34-LM", segmentCount, duration, label, T0);

    // ── Cluster() — the pure, synchronous grouping step ─────────────────────────────────────

    [Fact] // scenario: same voice appears in 3 different recordings — all 3 rows group into ONE voice
    public void Same_voice_across_multiple_recordings_clusters_together()
    {
        var corpus = new[]
        {
            Row("rec-1", 0, TestVectors.Axis(0), duration: 10.0),
            Row("rec-2", 0, TestVectors.TiltedFromAxis(0, 5, 0.95), duration: 8.0),
            Row("rec-3", 1, TestVectors.TiltedFromAxis(0, 6, 0.93), duration: 6.0),
        };

        var voices = VoiceReviewClusterer.Cluster(corpus);

        Assert.Single(voices);
        Assert.Equal(3, voices[0].Members.Count);
        Assert.Equal(3, voices[0].RecordingCount);
    }

    [Fact] // scenario: two distinct voices across recordings never merge into one
    public void Distinct_voices_stay_separate_across_recordings()
    {
        // Voice A on axis 0, voice B on axis 10 — orthogonal axes give cross-voice cosine 0.0
        // (unambiguously below cutoff) while each voice's own tilts stay mutually high (same-voice).
        var corpus = new[]
        {
            Row("rec-1", 0, TestVectors.Axis(0), duration: 10.0),                        // voice A
            Row("rec-2", 0, TestVectors.TiltedFromAxis(0, 5, 0.95), duration: 8.0),       // voice A (same)
            Row("rec-1", 1, TestVectors.Axis(10), duration: 20.0),                        // voice B
            Row("rec-3", 0, TestVectors.TiltedFromAxis(10, 11, 0.90), duration: 15.0),    // voice B (same)
        };

        var voices = VoiceReviewClusterer.Cluster(corpus);

        Assert.Equal(2, voices.Count);
        var voiceA = voices.Single(v => v.Members.Any(m => m.RecordingId == "rec-1" && m.ClusterIndex == 0));
        var voiceB = voices.Single(v => v.Members.Any(m => m.RecordingId == "rec-1" && m.ClusterIndex == 1));
        Assert.Equal(2, voiceA.Members.Count);
        Assert.Equal(2, voiceB.Members.Count);
        // Voice A's members never include voice B's row.
        Assert.DoesNotContain(voiceA.Members, m => m.RecordingId == "rec-1" && m.ClusterIndex == 1);
    }

    [Fact] // scenario: coverage stats (recording count, segment count, total duration) are correct
    public void Coverage_stats_are_correct()
    {
        var corpus = new[]
        {
            Row("rec-1", 0, TestVectors.Axis(0), segmentCount: 4, duration: 10.0),
            Row("rec-2", 0, TestVectors.TiltedFromAxis(0, 5, 0.95), segmentCount: 2, duration: 5.5),
            Row("rec-2", 1, TestVectors.TiltedFromAxis(0, 6, 0.93), segmentCount: 1, duration: 3.5), // same voice, same recording
        };

        var voices = VoiceReviewClusterer.Cluster(corpus);

        Assert.Single(voices);
        var voice = voices[0];
        Assert.Equal(3, voice.Members.Count);
        Assert.Equal(2, voice.RecordingCount);       // distinct recordings: rec-1, rec-2
        Assert.Equal(7, voice.SegmentCount);          // 4 + 2 + 1
        Assert.Equal(19.0, voice.TotalDurationSeconds, 3); // 10.0 + 5.5 + 3.5
    }

    [Fact] // scenario: representative centroid is the mean over all contributing members
    public void Representative_centroid_is_the_mean_of_contributing_members()
    {
        var a = TestVectors.Axis(0);
        var b = TestVectors.TiltedFromAxis(0, 5, 0.97);
        var corpus = new[]
        {
            Row("rec-1", 0, a),
            Row("rec-2", 0, b),
        };

        var voices = VoiceReviewClusterer.Cluster(corpus);

        Assert.Single(voices);
        var expected = Cosine.Centroid(new IReadOnlyList<float>[] { a, b });
        Assert.Equal(expected, voices[0].RepresentativeCentroid);
    }

    [Fact] // scenario: voices are RANKED by coverage — most-frequent/most-material first
    public void Voices_are_ranked_by_coverage_most_frequent_first()
    {
        // Voice A on axis 0 (low coverage); voice B on axis 20 (high coverage, 3 rows). Orthogonal
        // axes → cross-voice cosine 0.0 (never merges), same-axis tilts → high within-voice cosine.
        var corpus = new[]
        {
            // Voice A: low coverage (one row, short duration)
            Row("rec-1", 0, TestVectors.Axis(0), duration: 5.0),
            // Voice B: high coverage (three rows across three recordings, long total duration)
            Row("rec-2", 0, TestVectors.Axis(20), duration: 30.0),
            Row("rec-3", 0, TestVectors.TiltedFromAxis(20, 21, 0.90), duration: 25.0),
            Row("rec-4", 0, TestVectors.TiltedFromAxis(20, 22, 0.85), duration: 20.0),
        };

        var voices = VoiceReviewClusterer.Cluster(corpus);

        Assert.Equal(2, voices.Count);
        Assert.Equal("voice_00", voices[0].LocalId);
        Assert.Equal(3, voices[0].Members.Count);        // voice B ranked first (75s > 5s)
        Assert.Equal("voice_01", voices[1].LocalId);
        Assert.Single(voices[1].Members);                 // voice A ranked second
        Assert.True(voices[0].TotalDurationSeconds > voices[1].TotalDurationSeconds);
    }

    [Fact] // scenario: determinism — same input + cutoff always yields the same grouping AND local ids
    public void Clustering_is_deterministic_for_a_given_input_and_cutoff()
    {
        // A mix of near-duplicate and far-apart centroids (however many groups they settle into,
        // both runs over the SAME input must land on the exact same grouping + local ids).
        var corpus = new[]
        {
            Row("rec-1", 0, TestVectors.Axis(0), duration: 12.0),
            Row("rec-2", 0, TestVectors.TiltedFromAxis(0, 5, 0.95), duration: 9.0),
            Row("rec-3", 0, TestVectors.TiltedFromAxis(0, 15, 0.05), duration: 40.0),
            Row("rec-4", 1, TestVectors.TiltedFromAxis(0, 16, 0.06), duration: 33.0),
        };

        var run1 = VoiceReviewClusterer.Cluster(corpus);
        var run2 = VoiceReviewClusterer.Cluster(corpus);

        Assert.Equal(run1.Count, run2.Count);
        for (var i = 0; i < run1.Count; i++)
        {
            Assert.Equal(run1[i].LocalId, run2[i].LocalId);
            Assert.Equal(run1[i].RepresentativeCentroid, run2[i].RepresentativeCentroid);
            Assert.Equal(
                run1[i].Members.Select(m => (m.RecordingId, m.ClusterIndex)).OrderBy(k => k),
                run2[i].Members.Select(m => (m.RecordingId, m.ClusterIndex)).OrderBy(k => k));
        }
    }

    [Fact] // scenario: empty corpus yields no voices
    public void Empty_corpus_yields_no_voices()
    {
        Assert.Empty(VoiceReviewClusterer.Cluster(Array.Empty<RecordingVoiceprint>()));
    }

    [Fact] // scenario: default review cutoff is the documented 0.58 starting point
    public void Default_review_cutoff_is_058()
    {
        Assert.Equal(0.58, VoiceReviewClusterer.DefaultReviewCutoff);
    }

    [Fact] // scenario: the review cutoff is a DIFFERENT knob than ClusterMerge's within-recording 0.62
    public void Review_cutoff_is_distinct_from_the_within_recording_merge_cutoff()
    {
        Assert.NotEqual(ClusterMerge.DefaultMergeCutoff, VoiceReviewClusterer.DefaultReviewCutoff);
    }

    [Fact] // scenario: cutoff is configurable — a stricter cutoff can split what a looser one merges
    public void Cutoff_is_configurable_and_affects_grouping()
    {
        // cos ≈ 0.60 between the two rows.
        var corpus = new[]
        {
            Row("rec-1", 0, TestVectors.Axis(0)),
            Row("rec-2", 0, TestVectors.TiltedFromAxis(0, 5, 0.60)),
        };

        var loose = VoiceReviewClusterer.Cluster(corpus, reviewCutoff: 0.50);
        var strict = VoiceReviewClusterer.Cluster(corpus, reviewCutoff: 0.90);

        Assert.Single(loose);   // merged under the looser cutoff
        Assert.Equal(2, strict.Count); // split under the stricter cutoff
    }

    // ── ClusterAsync() — the store-backed, ranked-and-capped async entry point ──────────────

    [Fact] // scenario: async entry point reads the whole corpus via the store and ranks/caps it
    public async Task ClusterAsync_reads_the_corpus_store_and_returns_ranked_capped_candidates()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        await store.PersistAsync("rec-1", new[] { Row("rec-1", 0, TestVectors.Axis(0), duration: 5.0) });
        await store.PersistAsync("rec-2", new[]
        {
            Row("rec-2", 0, TestVectors.TiltedFromAxis(0, 20, 0.05), duration: 30.0),
        });

        var clusterer = new VoiceReviewClusterer(store);
        var voices = await clusterer.ClusterAsync();

        Assert.Equal(2, voices.Count);
        Assert.Equal("voice_00", voices[0].LocalId);
        Assert.Equal(30.0, voices[0].TotalDurationSeconds, 3); // higher-coverage voice ranked first
    }

    [Fact] // scenario: maxCandidates caps the output to the top-N by coverage
    public async Task ClusterAsync_caps_output_at_maxCandidates()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        for (var i = 0; i < 5; i++)
        {
            // Each recording contributes one WIDELY separated voice (axis i, i+50) so none merge.
            await store.PersistAsync($"rec-{i}", new[]
            {
                Row($"rec-{i}", 0, TestVectors.Axis(i * 10), duration: 10.0 * (i + 1)),
            });
        }

        var clusterer = new VoiceReviewClusterer(store);
        var voices = await clusterer.ClusterAsync(maxCandidates: 3);

        Assert.Equal(3, voices.Count);
        // Highest-duration voices are kept (50, 40, 30 → recordings 4, 3, 2).
        Assert.Equal(50.0, voices[0].TotalDurationSeconds, 3);
        Assert.Equal(40.0, voices[1].TotalDurationSeconds, 3);
        Assert.Equal(30.0, voices[2].TotalDurationSeconds, 3);
    }

    [Fact] // scenario: an already-enrolled voice is excluded so the operator only names unknowns
    public async Task ClusterAsync_excludes_a_voice_already_matching_an_enrolled_person()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        var enrolledCentroid = TestVectors.Axis(0);
        await store.PersistAsync("rec-1", new[] { Row("rec-1", 0, enrolledCentroid, duration: 10.0) });
        await store.PersistAsync("rec-2", new[]
        {
            Row("rec-2", 0, TestVectors.TiltedFromAxis(0, 20, 0.05), duration: 30.0), // unknown voice
        });

        var allowlist = new EnrollmentAllowlist(new[] { "marco" });
        var enrolledStore = new InMemoryVoiceprintStore(allowlist);
        await enrolledStore.EnrollOrRefineAsync(
            "marco", enrolledCentroid, sourceSegments: ["rec-1#0"], matchCosine: null, on: DateOnly.FromDateTime(T0.Date));

        var clusterer = new VoiceReviewClusterer(store, enrolledStore);
        var voices = await clusterer.ClusterAsync();

        Assert.Single(voices); // only the unknown voice remains — marco's is excluded
        Assert.Equal(30.0, voices[0].TotalDurationSeconds, 3);
    }

    [Fact] // scenario: with NO enrolled store supplied, nothing is excluded (offline / nobody enrolled yet)
    public async Task ClusterAsync_excludes_nothing_when_no_enrolled_store_supplied()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        await store.PersistAsync("rec-1", new[] { Row("rec-1", 0, TestVectors.Axis(0), duration: 10.0) });

        var clusterer = new VoiceReviewClusterer(store);
        var voices = await clusterer.ClusterAsync();

        Assert.Single(voices);
    }

    [Fact] // scenario: this is a REVIEW SNAPSHOT — nothing is persisted; repeated calls never mutate the corpus
    public async Task ClusterAsync_is_read_only_repeated_calls_never_mutate_the_corpus()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        await store.PersistAsync("rec-1", new[] { Row("rec-1", 0, TestVectors.Axis(0), duration: 10.0) });

        var clusterer = new VoiceReviewClusterer(store);
        var before = await store.GetCorpusAsync();
        _ = await clusterer.ClusterAsync();
        _ = await clusterer.ClusterAsync();
        var after = await store.GetCorpusAsync();

        Assert.Equal(before.Count, after.Count);
        Assert.Equal(
            before.Select(r => (r.RecordingId, r.ClusterIndex, r.CreatedAt)),
            after.Select(r => (r.RecordingId, r.ClusterIndex, r.CreatedAt)));
    }
}
