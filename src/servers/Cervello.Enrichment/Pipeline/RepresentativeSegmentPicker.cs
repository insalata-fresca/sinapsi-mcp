using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Pipeline;

/// <summary>
/// Pure, deterministic selection of the representative ~30s window for a voice out of its
/// contributing diarized segments (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V2, §4.1). Input is the segment ranges for
/// EVERY <c>(recordingId, clusterIndex)</c> member of a <see cref="VoiceReviewCluster"/> (fetched by
/// the caller via <see cref="Ports.IRecordingVoiceprintStore.GetSegmentsAsync"/> — this type does no
/// I/O). A single clip can only come from one recording's audio blob, so the algorithm:
///
/// <list type="number">
/// <item>groups segments by <see cref="DiarizedSegment.Speaker"/>-agnostic recording id (the caller
/// already scoped every input segment to one voice's members, so grouping by recording id alone is
/// correct here);</item>
/// <item>picks the recording with the most total segment duration (the richest source for that
/// voice — "highest-SNR proxy, most material for the operator to recognise", design §4.1);</item>
/// <item>within that recording, stitches consecutive/overlapping segments into contiguous runs and
/// returns the run with the most total speech, capped at <paramref name="maxWindowSeconds"/> — if the
/// richest run is longer than the cap, the window starts at the run's start and extends only to the
/// cap (never picks a mid-run offset, so the clip always begins on a real speech onset).</item>
/// </list>
///
/// <para><b>Determinism.</b> Segments are ordered by <c>(Start, End)</c> before grouping — the same
/// input segment set always yields the same window. No randomness, no wall-clock dependency.</para>
///
/// <para><b>Edge cases.</b> An empty segment list (no persisted ranges — e.g. a pre-V0 row) or a
/// segment list whose longest run is shorter than <paramref name="minWindowSeconds"/> returns
/// <see langword="null"/> — the caller MUST treat this as "cannot cut a clip for this voice" and
/// degrade gracefully (skip it / surface a gap), never fabricate a window.</para>
/// </summary>
public static class RepresentativeSegmentPicker
{
    /// <summary>Target/maximum window length — design §7 V2: "~30 s".</summary>
    public const double DefaultMaxWindowSeconds = 30.0;

    /// <summary>
    /// Below this, a clip is not worth cutting (too short to recognise a voice from) — the picker
    /// returns <see langword="null"/> rather than a near-empty clip.
    /// </summary>
    public const double DefaultMinWindowSeconds = 1.0;

    public static RepresentativeWindow? Pick(
        IReadOnlyList<(string RecordingId, DiarizedSegment Segment)> segments,
        double maxWindowSeconds = DefaultMaxWindowSeconds,
        double minWindowSeconds = DefaultMinWindowSeconds)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (maxWindowSeconds <= 0)
            throw new ArgumentException("maxWindowSeconds must be > 0", nameof(maxWindowSeconds));
        if (minWindowSeconds <= 0 || minWindowSeconds > maxWindowSeconds)
            throw new ArgumentException(
                "minWindowSeconds must be > 0 and <= maxWindowSeconds", nameof(minWindowSeconds));
        if (segments.Count == 0) return null;

        // Group by recording, ordered for determinism.
        var byRecording = segments
            .GroupBy(s => s.RecordingId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        RepresentativeWindow? best = null;
        double bestRunDuration = -1;

        foreach (var group in byRecording)
        {
            var ordered = group
                .Select(g => g.Segment)
                .OrderBy(s => s.Start)
                .ThenBy(s => s.End)
                .ToList();

            var run = LongestContiguousRun(ordered, maxWindowSeconds, minWindowSeconds);
            if (run is null) continue;

            var runDuration = run.Value.End - run.Value.Start;
            // Best recording = most total representative material. Tie-break on recording id for determinism.
            if (runDuration > bestRunDuration ||
                (runDuration == bestRunDuration && best is not null &&
                 string.CompareOrdinal(group.Key, best.RecordingId) < 0))
            {
                bestRunDuration = runDuration;
                best = new RepresentativeWindow(group.Key, run.Value.Start, run.Value.End);
            }
        }

        return best;
    }

    /// <summary>
    /// Stitch consecutive/overlapping segments (gap-tolerant — small silences between utterances by
    /// the same voice still count as one contiguous run) into runs, return the longest one capped at
    /// <paramref name="maxWindowSeconds"/>. Returns null if no run reaches <paramref name="minWindowSeconds"/>.
    /// </summary>
    private static (double Start, double End)? LongestContiguousRun(
        IReadOnlyList<DiarizedSegment> ordered, double maxWindowSeconds, double minWindowSeconds)
    {
        // Segments belonging to the SAME voice that are close together (<= this gap) are treated as
        // one contiguous run for clip-cutting purposes — a few hundred ms of inter-utterance silence
        // shouldn't fragment an otherwise-good window. Segments further apart start a new run.
        const double maxGapSeconds = 1.5;

        (double Start, double End)? bestRun = null;
        double bestDuration = -1;

        var runStart = ordered[0].Start;
        var runEnd = ordered[0].End;

        void ConsiderRun(double start, double end)
        {
            // Cap at maxWindowSeconds, anchored at the run's start (never a mid-run offset).
            var cappedEnd = System.Math.Min(end, start + maxWindowSeconds);
            var duration = cappedEnd - start;
            if (duration < minWindowSeconds) return;
            if (duration > bestDuration)
            {
                bestDuration = duration;
                bestRun = (start, cappedEnd);
            }
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            var seg = ordered[i];
            if (seg.Start - runEnd <= maxGapSeconds)
            {
                // Extend the current run.
                runEnd = System.Math.Max(runEnd, seg.End);
            }
            else
            {
                ConsiderRun(runStart, runEnd);
                runStart = seg.Start;
                runEnd = seg.End;
            }
        }
        ConsiderRun(runStart, runEnd);

        return bestRun;
    }
}
