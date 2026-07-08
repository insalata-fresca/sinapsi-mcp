namespace Cervello.Enrichment.Domain;

/// <summary>
/// The minimal handle the enrichment engine needs for a normalized recording: its id, the
/// audio sha256 (both form the idempotency key), the audio format + language for transcription,
/// the OPTIONAL Google <c>.txt</c> transcript's staged content sha (the ratified base), and
/// whether the WATCH-side ready marker is set. The audio + transcript bytes themselves stay in the
/// CT staging blob store and are fetched transiently by a stage — never carried in git-side state.
/// </summary>
public sealed record RecordingRef
{
    public RecordingRef(
        string id,
        string audioSha256,
        string format,
        string language,
        bool ready,
        string? googleTxtSha256 = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("RecordingRef.Id must be non-empty", nameof(id));
        if (string.IsNullOrWhiteSpace(audioSha256))
            throw new ArgumentException("RecordingRef.AudioSha256 must be non-empty", nameof(audioSha256));
        if (string.IsNullOrWhiteSpace(format))
            throw new ArgumentException("RecordingRef.Format must be non-empty", nameof(format));
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("RecordingRef.Language must be non-empty", nameof(language));
        Id = id;
        AudioSha256 = audioSha256;
        Format = format;
        Language = language;
        Ready = ready;
        // Blank → no Google .txt for this recording (normalise to null so the base source degrades).
        GoogleTxtSha256 = string.IsNullOrWhiteSpace(googleTxtSha256) ? null : googleTxtSha256;
    }

    public string Id { get; }
    public string AudioSha256 { get; }
    public string Format { get; }
    public string Language { get; }
    public bool Ready { get; }

    /// <summary>
    /// The staged content sha256 of the recording's paired Google <c>.txt</c> transcript (the
    /// ratified base), or <see langword="null"/> when the recording has no Google transcript. The
    /// live <c>IBaseTranscriptSource</c> reads the staged <c>.txt</c> blob by this sha (same
    /// content-addressed staging layout as the audio). Never the transcript bytes themselves.
    /// </summary>
    public string? GoogleTxtSha256 { get; }

    /// <summary>The SCHEMAS §5 idempotency key: <c>rec:&lt;recordingId&gt;:&lt;audio-sha256&gt;</c>.</summary>
    public string IdempotencyKey => $"rec:{Id}:{AudioSha256}";
}
