using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Voiceprint-naming V2 — the representative-window picker (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V2, §4.1). Proves: the longest contiguous run
/// wins; a run longer than the cap is truncated (never a mid-run offset); close-together segments
/// stitch into one run while far-apart segments split into separate runs; the richest RECORDING
/// wins when a voice spans multiple recordings; determinism for a fixed input; graceful null on no
/// segments / all-too-short segments.
/// </summary>
public sealed class RepresentativeSegmentPickerTests
{
    private static (string, DiarizedSegment) Seg(string recordingId, double start, double end, string speaker = "s1") =>
        (recordingId, new DiarizedSegment(speaker, start, end));

    [Fact] // scenario: one recording, one long segment — picked verbatim (under the cap)
    public void Single_segment_under_cap_is_picked_verbatim()
    {
        var segments = new[] { Seg("rec-1", 10.0, 22.0) };

        var window = RepresentativeSegmentPicker.Pick(segments);

        Assert.NotNull(window);
        Assert.Equal("rec-1", window!.RecordingId);
        Assert.Equal(10.0, window.Start);
        Assert.Equal(22.0, window.End);
        Assert.Equal(12.0, window.DurationSeconds);
    }

    [Fact] // scenario: close-together segments (small gaps) stitch into one contiguous run
    public void Close_segments_stitch_into_one_run()
    {
        var segments = new[]
        {
            Seg("rec-1", 0.0, 5.0),
            Seg("rec-1", 5.8, 9.0),   // 0.8s gap — within the stitch tolerance
            Seg("rec-1", 9.5, 14.0),  // 0.5s gap — within tolerance
        };

        var window = RepresentativeSegmentPicker.Pick(segments);

        Assert.NotNull(window);
        Assert.Equal(0.0, window!.Start);
        Assert.Equal(14.0, window.End);
    }

    [Fact] // scenario: far-apart segments split into separate runs; the LONGER run wins
    public void Distant_segments_split_into_separate_runs_longest_wins()
    {
        var segments = new[]
        {
            Seg("rec-1", 0.0, 3.0),    // run A: 3s total
            Seg("rec-1", 50.0, 60.0),  // run B: 10s total, far from A (>1.5s gap)
        };

        var window = RepresentativeSegmentPicker.Pick(segments);

        Assert.NotNull(window);
        Assert.Equal(50.0, window!.Start);
        Assert.Equal(60.0, window.End);
    }

    [Fact] // scenario: a run longer than the cap is truncated to the cap, anchored at the run start
    public void Run_longer_than_cap_is_truncated_at_run_start()
    {
        var segments = new[] { Seg("rec-1", 100.0, 160.0) }; // 60s run

        var window = RepresentativeSegmentPicker.Pick(segments, maxWindowSeconds: 30.0);

        Assert.NotNull(window);
        Assert.Equal(100.0, window!.Start);
        Assert.Equal(130.0, window.End); // capped at start+30, never a mid-run offset
        Assert.Equal(30.0, window.DurationSeconds);
    }

    [Fact] // scenario: a voice spans multiple recordings — the recording with the most representative material wins
    public void Multiple_recordings_richest_recording_wins()
    {
        var segments = new[]
        {
            Seg("rec-A", 0.0, 5.0),    // rec-A: 5s
            Seg("rec-B", 0.0, 20.0),   // rec-B: 20s — richer
            Seg("rec-C", 0.0, 8.0),    // rec-C: 8s
        };

        var window = RepresentativeSegmentPicker.Pick(segments);

        Assert.NotNull(window);
        Assert.Equal("rec-B", window!.RecordingId);
    }

    [Fact] // scenario: tie on duration across recordings — deterministic tie-break on recording id (ordinal)
    public void Tie_across_recordings_breaks_on_recording_id()
    {
        var segments = new[]
        {
            Seg("rec-Z", 0.0, 10.0),
            Seg("rec-A", 0.0, 10.0),
        };

        var window = RepresentativeSegmentPicker.Pick(segments);

        Assert.NotNull(window);
        Assert.Equal("rec-A", window!.RecordingId);
    }

    [Fact] // scenario: same input, called twice — identical result (no randomness / no ordering dependency)
    public void Deterministic_for_fixed_input()
    {
        var segments = new[]
        {
            Seg("rec-1", 12.3, 18.1),
            Seg("rec-1", 40.0, 41.0),
            Seg("rec-2", 5.0, 5.5),
        };

        var w1 = RepresentativeSegmentPicker.Pick(segments);
        var w2 = RepresentativeSegmentPicker.Pick(segments.Reverse().ToArray()); // shuffled input order

        Assert.Equal(w1, w2);
    }

    [Fact] // scenario: no segments at all (e.g. a pre-V0 row with no persisted ranges) — graceful null, never fabricate
    public void No_segments_returns_null()
    {
        var window = RepresentativeSegmentPicker.Pick(Array.Empty<(string, DiarizedSegment)>());

        Assert.Null(window);
    }

    [Fact] // scenario: every segment/run is shorter than the minimum — graceful null, never a near-empty clip
    public void All_runs_below_minimum_returns_null()
    {
        var segments = new[] { Seg("rec-1", 0.0, 0.2) }; // 0.2s, far below the 1.0s default minimum

        var window = RepresentativeSegmentPicker.Pick(segments);

        Assert.Null(window);
    }

    [Fact] // scenario: a caller-tuned min/max window is honoured
    public void Custom_min_and_max_window_are_honoured()
    {
        var segments = new[] { Seg("rec-1", 0.0, 5.0) };

        Assert.Null(RepresentativeSegmentPicker.Pick(segments, minWindowSeconds: 10.0));
        var window = RepresentativeSegmentPicker.Pick(segments, maxWindowSeconds: 3.0, minWindowSeconds: 1.0);
        Assert.NotNull(window);
        Assert.Equal(3.0, window!.DurationSeconds);
    }

    [Fact]
    public void Invalid_window_bounds_throw()
    {
        var segments = new[] { Seg("rec-1", 0.0, 5.0) };
        Assert.Throws<ArgumentException>(() => RepresentativeSegmentPicker.Pick(segments, maxWindowSeconds: 0));
        Assert.Throws<ArgumentException>(() => RepresentativeSegmentPicker.Pick(segments, minWindowSeconds: 0));
        Assert.Throws<ArgumentException>(() =>
            RepresentativeSegmentPicker.Pick(segments, maxWindowSeconds: 5, minWindowSeconds: 10));
    }
}
