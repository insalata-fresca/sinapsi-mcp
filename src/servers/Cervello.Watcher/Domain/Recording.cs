namespace Cervello.Watcher.Domain;

/// <summary>
/// A recording ready for NORMALIZE. In the MIXED-cases model a recording may carry
/// both sides, audio-only, or transcript-only:
/// <list type="bullet">
///   <item><b>both</b> — <see cref="AudioSha256"/> + <see cref="AudioDriveId"/> set, plus a
///     <see cref="TxtDriveId"/> / <see cref="TranscriptSha256"/> (the Google <c>.txt</c>);</item>
///   <item><b>audio-only</b> — audio set, transcript sides empty/null;</item>
///   <item><b>transcript-only</b> — audio sides empty, <see cref="TxtDriveId"/> +
///     <see cref="TranscriptSha256"/> set.</item>
/// </list>
/// At least one side must be present. Immutable. The dedupe/idempotency key
/// (<see cref="RecordingKey"/>) is deterministic and stable across both components: it uses the
/// audio sha when audio is present, else the transcript sha (prefixed <c>txt:</c> to keep the two
/// families disjoint), so a single-sided recording still has a unique, reproducible key.
/// </summary>
public sealed record Recording
{
    public Recording(
        string id,
        string basename,
        string audioSha256,
        string audioDriveId,
        string? txtDriveId,
        string recordedAt,
        PipelineState state,
        string? transcriptSha256 = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Recording.Id must be non-empty", nameof(id));
        if (string.IsNullOrWhiteSpace(basename))
            throw new ArgumentException("Recording.Basename must be non-empty", nameof(basename));
        if (string.IsNullOrWhiteSpace(recordedAt))
            throw new ArgumentException("Recording.RecordedAt must be non-empty", nameof(recordedAt));

        // Normalise empty → "" so column NOT NULL constraints (audio_sha256 / audio_drive_id) are
        // satisfied for a single-sided recording without a schema change.
        AudioSha256 = audioSha256 ?? "";
        AudioDriveId = audioDriveId ?? "";
        TxtDriveId = string.IsNullOrWhiteSpace(txtDriveId) ? null : txtDriveId;
        TranscriptSha256 = string.IsNullOrWhiteSpace(transcriptSha256) ? null : transcriptSha256;

        var hasAudio = !string.IsNullOrWhiteSpace(AudioSha256);
        var hasTranscript = TranscriptSha256 is not null || TxtDriveId is not null;
        if (!hasAudio && !hasTranscript)
            throw new ArgumentException(
                "Recording requires at least one side (audio_sha256 or a transcript)", nameof(audioSha256));
        // An audio side must carry its Drive id (the manifest source_drive_id / staging custody).
        if (hasAudio && string.IsNullOrWhiteSpace(AudioDriveId))
            throw new ArgumentException(
                "Recording.AudioDriveId must be non-empty when audio is present", nameof(audioDriveId));

        Id = id;
        Basename = basename;
        RecordedAt = recordedAt;
        State = state;
    }

    public string Id { get; }
    public string Basename { get; }

    /// <summary>The audio blob content sha, or <c>""</c> for a transcript-only recording.</summary>
    public string AudioSha256 { get; }

    /// <summary>The audio Drive fileId, or <c>""</c> for a transcript-only recording.</summary>
    public string AudioDriveId { get; }

    public string? TxtDriveId { get; }

    /// <summary>The staged Google <c>.txt</c> content sha, or <see langword="null"/> when absent.</summary>
    public string? TranscriptSha256 { get; }

    /// <summary>Deterministic <c>yyyy-MM-ddTHH:mm</c> (D5). Derived from the audio Drive createdTime
    /// when audio is present, else the transcript's createdTime.</summary>
    public string RecordedAt { get; }
    public PipelineState State { get; }

    /// <summary>True iff this recording carries an audio side.</summary>
    public bool HasAudio => !string.IsNullOrWhiteSpace(AudioSha256);

    /// <summary>
    /// The stable content-sha the idempotency key is keyed on: the audio sha when audio is present,
    /// else the transcript sha. Always non-empty (the ctor guarantees at least one side).
    /// </summary>
    public string KeySha => HasAudio ? AudioSha256 : TranscriptSha256!;

    /// <summary>
    /// The dedupe key for the manifest + the shared §8/§5 idempotency key. For an audio recording:
    /// <c>rec:&lt;id&gt;:&lt;audio_sha256&gt;</c> (unchanged). For a transcript-only recording (no audio):
    /// <c>rec:&lt;id&gt;:txt:&lt;transcript_sha256&gt;</c> — the <c>txt:</c> prefix keeps the two families
    /// disjoint and the key reproducible from the persisted row by both the Watcher and the drain.
    /// </summary>
    public string RecordingKey => HasAudio ? $"rec:{Id}:{AudioSha256}" : $"rec:{Id}:txt:{TranscriptSha256}";
}
