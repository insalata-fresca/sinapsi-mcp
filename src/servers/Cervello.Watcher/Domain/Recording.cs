namespace Cervello.Watcher.Domain;

/// <summary>
/// A paired recording (audio + transcript, same basename) ready for NORMALIZE.
/// Immutable; the id / checksum / drive ids are all required and non-empty.
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
        PipelineState state)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Recording.Id must be non-empty", nameof(id));
        if (string.IsNullOrWhiteSpace(basename))
            throw new ArgumentException("Recording.Basename must be non-empty", nameof(basename));
        if (string.IsNullOrWhiteSpace(audioSha256))
            throw new ArgumentException("Recording.AudioSha256 must be non-empty", nameof(audioSha256));
        if (string.IsNullOrWhiteSpace(audioDriveId))
            throw new ArgumentException("Recording.AudioDriveId must be non-empty", nameof(audioDriveId));
        if (string.IsNullOrWhiteSpace(recordedAt))
            throw new ArgumentException("Recording.RecordedAt must be non-empty", nameof(recordedAt));
        Id = id;
        Basename = basename;
        AudioSha256 = audioSha256;
        AudioDriveId = audioDriveId;
        TxtDriveId = txtDriveId;
        RecordedAt = recordedAt;
        State = state;
    }

    public string Id { get; }
    public string Basename { get; }
    public string AudioSha256 { get; }
    public string AudioDriveId { get; }
    public string? TxtDriveId { get; }

    /// <summary>Deterministic <c>yyyy-MM-ddTHH:mm</c> from the audio Drive createdTime (D5).</summary>
    public string RecordedAt { get; }
    public PipelineState State { get; }

    /// <summary>The dedupe key for the manifest: <c>rec:&lt;recordingId&gt;:&lt;audio_sha256&gt;</c>.</summary>
    public string RecordingKey => $"rec:{Id}:{AudioSha256}";
}
