namespace Cervello.Enrichment.Domain;

/// <summary>
/// The result of picking a representative ~30s window for a voice out of its contributing diarized
/// segments (design <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7 phase V2, §4.1:
/// "Pick the representative segment per voice: the longest single diarized segment (or a contiguous
/// ~30s window) among the voice's contributing clusters"). Always describes a window into exactly
/// ONE recording — a voice's segments may span multiple recordings (<see cref="VoiceReviewCluster"/>),
/// but a single clip can only be cut from one audio blob, so the picker selects the best recording
/// first (most total representative-window duration) then the best window within it.
/// </summary>
public sealed record RepresentativeWindow
{
    public RepresentativeWindow(string recordingId, double start, double end)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("RepresentativeWindow.RecordingId must be non-empty", nameof(recordingId));
        if (end <= start)
            throw new ArgumentException("RepresentativeWindow.End must be > Start", nameof(end));
        RecordingId = recordingId;
        Start = start;
        End = end;
    }

    /// <summary>The recording this window is cut from — the audio blob to fetch is this recording's staged audio.</summary>
    public string RecordingId { get; }

    /// <summary>Window start, seconds, within the recording.</summary>
    public double Start { get; }

    /// <summary>Window end, seconds, within the recording.</summary>
    public double End { get; }

    /// <summary>Window length, seconds.</summary>
    public double DurationSeconds => End - Start;
}
