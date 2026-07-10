namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for resolving a bare recording id (as carried on a <c>recording_voiceprints</c> row / a
/// <see cref="Domain.VoiceReviewMember"/>) to the audio blob coordinates <see cref="IAudioSource"/>
/// needs to fetch its bytes (design <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7
/// phase V4, §4 "resolving recordingId → audio is V4's orchestration job").
///
/// <para><b>Not a new store — a read-only view over the Watcher's own table.</b> The audio sha256 +
/// format for a recording already live in <c>watcher_recording</c> (the same table
/// <see cref="Host.Drain.PgNormalizedWorkQueue"/> reads for the drain lease) — this port is a second,
/// independent READ-ONLY query against that same Watcher-owned row, keyed by <c>recording_id</c>
/// instead of <c>state</c>. It creates no new table and writes nothing.</para>
///
/// <para>Returns <see langword="null"/> for an unknown recording id or a recording with no audio
/// side (transcript-only) — the caller (V4 orchestration) treats this as "cannot resolve this
/// voice's audio, skip it", never fabricates a placeholder.</para>
/// </summary>
public interface IRecordingAudioRefResolver
{
    Task<RecordingAudioRef?> ResolveAsync(string recordingId, CancellationToken ct = default);
}

/// <summary>The audio blob coordinates <see cref="IAudioSource.FetchAsync"/> needs for one recording.</summary>
public sealed record RecordingAudioRef
{
    public RecordingAudioRef(string recordingId, string audioSha256, string format)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("RecordingAudioRef.RecordingId must be non-empty", nameof(recordingId));
        if (string.IsNullOrWhiteSpace(audioSha256))
            throw new ArgumentException("RecordingAudioRef.AudioSha256 must be non-empty", nameof(audioSha256));
        if (string.IsNullOrWhiteSpace(format))
            throw new ArgumentException("RecordingAudioRef.Format must be non-empty", nameof(format));
        RecordingId = recordingId;
        AudioSha256 = audioSha256;
        Format = format;
    }

    public string RecordingId { get; }
    public string AudioSha256 { get; }
    public string Format { get; }
}
