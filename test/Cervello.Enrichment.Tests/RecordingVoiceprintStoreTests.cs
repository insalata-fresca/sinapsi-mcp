using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The M3 corpus store's persistence invariants (design <c>ste/cervello</c>
/// <c>docs/design/autonomous-attribution.md</c> §4.1/§5 M3): round-trip, idempotent upsert on
/// <c>(recordingId, clusterIndex)</c>, and a corpus-wide fetch across many recordings — the raw
/// material M4's cross-recording resolver reads. Exercised against <see cref="InMemoryRecordingVoiceprintStore"/>
/// (the same contract <see cref="Adapters.PgRecordingVoiceprintStore"/> honours; that adapter's SQL is
/// covered by the opt-in offline pgvector IT in <c>PgAdaptersIntegrationTests</c>).
///
/// <para>SYNTHETIC 256-d vectors only — no personal audio, no real biometric vector ever appears here.</para>
/// </summary>
public sealed class RecordingVoiceprintStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

    private static RecordingVoiceprint Row(
        string recordingId, int clusterIndex, float[] centroid, string label,
        int segmentCount = 3, double duration = 12.5, DateTimeOffset? createdAt = null,
        IReadOnlyList<DiarizedSegment>? segments = null) =>
        new(recordingId, clusterIndex, centroid, "pyannote/wespeaker-voxceleb-resnet34-LM", segmentCount, duration, label,
            createdAt ?? T0, segments);

    // ---- round-trip -------------------------------------------------------------------

    [Fact] // scenario: persist a recording's merged clusters, then fetch them back verbatim
    public async Task Persist_then_get_for_recording_round_trips_every_field()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        var rows = new[]
        {
            Row("rec-1", 0, TestVectors.Axis(0), "s1", segmentCount: 4, duration: 20.0),
            Row("rec-1", 1, TestVectors.Axis(5), "s3", segmentCount: 2, duration: 8.25),
        };

        await store.PersistAsync("rec-1", rows);
        var fetched = await store.GetForRecordingAsync("rec-1");

        Assert.Equal(2, fetched.Count);
        // Ordered by ClusterIndex.
        Assert.Equal(0, fetched[0].ClusterIndex);
        Assert.Equal(1, fetched[1].ClusterIndex);
        Assert.Equal(rows[0].Centroid, fetched[0].Centroid);
        Assert.Equal("pyannote/wespeaker-voxceleb-resnet34-LM", fetched[0].Model);
        Assert.Equal(4, fetched[0].SegmentCount);
        Assert.Equal(20.0, fetched[0].DurationSeconds);
        Assert.Equal("s1", fetched[0].MergedSpeakerLabel); // descriptive metadata only, not the key
        Assert.Equal("rec-1", fetched[0].RecordingId);
    }

    [Fact]
    public async Task Get_for_unknown_recording_returns_empty()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        await store.PersistAsync("rec-1", [Row("rec-1", 0, TestVectors.Axis(0), "s1")]);

        Assert.Empty(await store.GetForRecordingAsync("rec-unknown"));
    }

    [Fact] // an empty cluster list (a recording with zero merged clusters) is a legal no-op
    public async Task Persist_empty_list_is_a_legal_noop()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        await store.PersistAsync("rec-1", Array.Empty<RecordingVoiceprint>());

        Assert.Empty(await store.GetForRecordingAsync("rec-1"));
        Assert.Empty(await store.GetCorpusAsync());
    }

    // ---- idempotent upsert on (recordingId, clusterIndex) -------------------------------

    [Fact] // scenario: re-processing the SAME recording (replay) upserts, never duplicates
    public async Task Repersisting_the_same_recording_upserts_not_duplicates()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        await store.PersistAsync("rec-1", [Row("rec-1", 0, TestVectors.Axis(0), "s1", segmentCount: 2)]);
        Assert.Single(await store.GetForRecordingAsync("rec-1"));

        // Re-process: same (recordingId, clusterIndex) key, refined centroid + more segments.
        await store.PersistAsync("rec-1",
            [Row("rec-1", 0, TestVectors.TiltedFromAxis(0, 1, 0.95), "s1", segmentCount: 5, duration: 30.0)]);

        var fetched = await store.GetForRecordingAsync("rec-1");
        Assert.Single(fetched); // still one row — upsert, not a duplicate
        Assert.Equal(5, fetched[0].SegmentCount);       // reflects the LATEST persist
        Assert.Equal(30.0, fetched[0].DurationSeconds);
    }

    [Fact] // a DIFFERENT clusterIndex on the same recording is a distinct row, never collapsed
    public async Task Different_cluster_index_same_recording_are_distinct_rows()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        await store.PersistAsync("rec-1",
        [
            Row("rec-1", 0, TestVectors.Axis(0), "s1"),
            Row("rec-1", 1, TestVectors.Axis(50), "s2"),
        ]);

        Assert.Equal(2, (await store.GetForRecordingAsync("rec-1")).Count);
    }

    [Fact] // the diarizer label is NEVER the identity — two recordings can both have a merged "s1"
    public async Task Merged_speaker_label_collision_across_recordings_does_not_collide_identity()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        // Both recordings' first merged cluster carries the SAME diarizer label "s1" — but they are
        // DIFFERENT people/voices (different centroids) and MUST persist as two distinct rows keyed
        // by (recordingId, clusterIndex), never merged/collided on the label.
        await store.PersistAsync("rec-a", [Row("rec-a", 0, TestVectors.Axis(0), "s1")]);
        await store.PersistAsync("rec-b", [Row("rec-b", 0, TestVectors.Axis(99), "s1")]);

        var corpus = await store.GetCorpusAsync();
        Assert.Equal(2, corpus.Count);
        Assert.Contains(corpus, r => r.RecordingId == "rec-a" && r.Centroid[0] == 1f);
        Assert.Contains(corpus, r => r.RecordingId == "rec-b" && r.Centroid[99] == 1f);
    }

    // ---- corpus-wide, multi-recording query ----------------------------------------------

    [Fact] // scenario: a corpus-wide fetch returns centroids across MANY recordings, coexisting
    public async Task Corpus_wide_query_returns_centroids_across_many_recordings()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        await store.PersistAsync("rec-1",
        [
            Row("rec-1", 0, TestVectors.Axis(0), "s1"),
            Row("rec-1", 1, TestVectors.Axis(10), "s2"),
        ]);
        await store.PersistAsync("rec-2", [Row("rec-2", 0, TestVectors.Axis(20), "s1")]);
        await store.PersistAsync("rec-3", [Row("rec-3", 0, TestVectors.Axis(30), "s1")]);

        var corpus = await store.GetCorpusAsync();

        Assert.Equal(4, corpus.Count); // 2 + 1 + 1, all coexisting
        Assert.Equal(new[] { "rec-1", "rec-1", "rec-2", "rec-3" },
            corpus.Select(r => r.RecordingId).ToArray()); // deterministic ordering: (RecordingId, ClusterIndex)
        Assert.Equal(new[] { 0, 1, 0, 0 }, corpus.Select(r => r.ClusterIndex).ToArray());
    }

    // ---- construction guards --------------------------------------------------------------

    [Fact]
    public void Constructing_a_row_with_a_wrong_dimension_centroid_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new RecordingVoiceprint("rec-1", 0, new float[10], "m", 1, 1.0, "s1", T0));
    }

    [Fact]
    public void Constructing_a_row_with_a_negative_cluster_index_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new RecordingVoiceprint("rec-1", -1, TestVectors.Axis(0), "m", 1, 1.0, "s1", T0));
    }

    [Fact] // PersistAsync guards against a caller passing rows for a DIFFERENT recording by mistake
    public async Task Persist_rejects_a_row_whose_recording_id_does_not_match_the_argument()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PersistAsync("rec-1", [Row("rec-OTHER", 0, TestVectors.Axis(0), "s1")]));
    }

    // ---- V0 segment-range persistence (design ste/cervello docs/design/voiceprint-naming.md §1.1/§5) ----

    [Fact] // scenario: a cluster's segment {start,end} ranges round-trip through persist/get, in order
    public async Task Persist_then_get_for_recording_round_trips_segment_ranges()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        var segments = new DiarizedSegment[]
        {
            new("s1", 0.0, 5.0),
            new("s1", 12.0, 30.5),
        };
        await store.PersistAsync("rec-1", [Row("rec-1", 0, TestVectors.Axis(0), "s1", segments: segments)]);

        var fetched = await store.GetForRecordingAsync("rec-1");

        Assert.Equal(2, fetched[0].Segments.Count);
        Assert.Equal(0.0, fetched[0].Segments[0].Start);
        Assert.Equal(5.0, fetched[0].Segments[0].End);
        Assert.Equal(12.0, fetched[0].Segments[1].Start);
        Assert.Equal(30.5, fetched[0].Segments[1].End);
    }

    [Fact] // a row persisted with no segments (legacy/pre-V0, or a caller that omits them) round-trips empty
    public async Task Persist_with_no_segments_round_trips_an_empty_list()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        await store.PersistAsync("rec-1", [Row("rec-1", 0, TestVectors.Axis(0), "s1")]);

        var fetched = await store.GetForRecordingAsync("rec-1");

        Assert.Empty(fetched[0].Segments);
    }

    [Fact] // scenario: re-processing the SAME recording (replay) upserts segment ranges too, never duplicates
    public async Task Repersisting_the_same_recording_upserts_segment_ranges_not_duplicates()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        await store.PersistAsync("rec-1",
            [Row("rec-1", 0, TestVectors.Axis(0), "s1", segments: [new DiarizedSegment("s1", 0.0, 5.0)])]);

        // Re-process: same (recordingId, clusterIndex) key, a DIFFERENT (refined) segment set —
        // e.g. a re-run of diarize-embed yields different boundaries. The latest persist wins wholesale.
        var refinedSegments = new DiarizedSegment[]
        {
            new("s1", 0.0, 6.0),
            new("s1", 10.0, 20.0),
            new("s1", 25.0, 40.0),
        };
        await store.PersistAsync("rec-1",
            [Row("rec-1", 0, TestVectors.TiltedFromAxis(0, 1, 0.95), "s1", segments: refinedSegments)]);

        var fetched = await store.GetForRecordingAsync("rec-1");
        Assert.Single(fetched); // still one centroid row — upsert, not a duplicate
        Assert.Equal(3, fetched[0].Segments.Count); // reflects the LATEST persist, not a union with the old
        Assert.Equal(6.0, fetched[0].Segments[0].End);
    }

    [Fact] // segments are retrievable per (recordingId, clusterIndex) via the dedicated read method
    public async Task GetSegmentsAsync_returns_the_ranges_for_one_cluster_only()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        await store.PersistAsync("rec-1",
        [
            Row("rec-1", 0, TestVectors.Axis(0), "s1", segments: [new DiarizedSegment("s1", 1.0, 2.0)]),
            Row("rec-1", 1, TestVectors.Axis(10), "s2", segments: [new DiarizedSegment("s2", 3.0, 4.0)]),
        ]);

        var seg0 = await store.GetSegmentsAsync("rec-1", 0);
        var seg1 = await store.GetSegmentsAsync("rec-1", 1);

        Assert.Single(seg0);
        Assert.Equal(1.0, seg0[0].Start);
        Assert.Single(seg1);
        Assert.Equal(3.0, seg1[0].Start);
    }

    [Fact] // an unknown (recordingId, clusterIndex) returns empty, never throws
    public async Task GetSegmentsAsync_for_unknown_cluster_returns_empty()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        await store.PersistAsync("rec-1",
            [Row("rec-1", 0, TestVectors.Axis(0), "s1", segments: [new DiarizedSegment("s1", 1.0, 2.0)])]);

        Assert.Empty(await store.GetSegmentsAsync("rec-1", 99));
        Assert.Empty(await store.GetSegmentsAsync("rec-unknown", 0));
    }

    [Fact] // GetCorpusAsync attaches the right segment ranges to the right cluster across many recordings
    public async Task GetCorpusAsync_attaches_segments_per_cluster_across_recordings()
    {
        var store = new InMemoryRecordingVoiceprintStore();
        await store.PersistAsync("rec-1",
        [
            Row("rec-1", 0, TestVectors.Axis(0), "s1", segments: [new DiarizedSegment("s1", 0.0, 1.0)]),
            Row("rec-1", 1, TestVectors.Axis(10), "s2", segments: [new DiarizedSegment("s2", 2.0, 3.0)]),
        ]);
        await store.PersistAsync("rec-2",
            [Row("rec-2", 0, TestVectors.Axis(20), "s1", segments: [new DiarizedSegment("s1", 4.0, 5.0)])]);

        var corpus = await store.GetCorpusAsync();

        var rec1c0 = corpus.Single(r => r.RecordingId == "rec-1" && r.ClusterIndex == 0);
        var rec1c1 = corpus.Single(r => r.RecordingId == "rec-1" && r.ClusterIndex == 1);
        var rec2c0 = corpus.Single(r => r.RecordingId == "rec-2" && r.ClusterIndex == 0);
        Assert.Equal(0.0, rec1c0.Segments.Single().Start);
        Assert.Equal(2.0, rec1c1.Segments.Single().Start);
        Assert.Equal(4.0, rec2c0.Segments.Single().Start);
    }
}
