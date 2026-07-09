namespace Cervello.Enrichment.Domain;

/// <summary>
/// The minimal handle the enrichment engine needs for a normalized recording: its id, the
/// audio sha256, the audio format + language for transcription, the OPTIONAL Google <c>.txt</c>
/// transcript's staged content sha (the ratified base), and whether the WATCH-side ready marker is
/// set. The audio + transcript bytes themselves stay in the CT staging blob store and are fetched
/// transiently by a stage — never carried in git-side state.
///
/// <para><b>MIXED cases.</b> A recording may be audio+transcript (both), audio-only, or
/// transcript-only. For a TRANSCRIPT-ONLY recording there is no audio, so <see cref="AudioSha256"/>
/// is empty and <see cref="HasAudio"/> is false — the orchestrator then SKIPS the audio-dependent
/// stages (fetch/diarize/merge/attribution) and keys the recording on the transcript sha
/// (<c>rec:&lt;id&gt;:txt:&lt;transcript_sha&gt;</c>) so the shared §5/§8 key round-trips with the
/// Watcher's <c>recording_key</c>. At least one side (audio sha OR google-txt sha) must be present.</para>
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
        if (string.IsNullOrWhiteSpace(format))
            throw new ArgumentException("RecordingRef.Format must be non-empty", nameof(format));
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("RecordingRef.Language must be non-empty", nameof(language));

        // Empty audio sha ⇒ transcript-only (no audio blob). Normalise blanks to "" / null.
        AudioSha256 = string.IsNullOrWhiteSpace(audioSha256) ? "" : audioSha256;
        // Blank → no Google .txt for this recording (normalise to null so the base source degrades).
        GoogleTxtSha256 = string.IsNullOrWhiteSpace(googleTxtSha256) ? null : googleTxtSha256;

        // At least one side must exist: audio present, OR a google .txt (transcript-only).
        if (!HasAudio && GoogleTxtSha256 is null)
            throw new ArgumentException(
                "RecordingRef requires at least one side: a non-empty audioSha256 or a googleTxtSha256 " +
                "(transcript-only). A recording with neither is not enrichable.", nameof(audioSha256));

        Id = id;
        Format = format;
        Language = language;
        Ready = ready;
    }

    public string Id { get; }

    /// <summary>The audio blob content sha, or <c>""</c> for a transcript-only recording.</summary>
    public string AudioSha256 { get; }

    public string Format { get; }
    public string Language { get; }
    public bool Ready { get; }

    /// <summary>True iff this recording carries an audio side (drives whether the audio stages run).</summary>
    public bool HasAudio => !string.IsNullOrEmpty(AudioSha256);

    /// <summary>
    /// The staged content sha256 of the recording's paired Google <c>.txt</c> transcript (the
    /// ratified base), or <see langword="null"/> when the recording has no Google transcript. The
    /// live <c>IBaseTranscriptSource</c> reads the staged <c>.txt</c> blob by this sha (same
    /// content-addressed staging layout as the audio). Never the transcript bytes themselves.
    /// </summary>
    public string? GoogleTxtSha256 { get; }

    /// <summary>
    /// The SCHEMAS §5 idempotency key. Audio recording: <c>rec:&lt;id&gt;:&lt;audio-sha256&gt;</c>
    /// (unchanged). Transcript-only: <c>rec:&lt;id&gt;:txt:&lt;transcript-sha256&gt;</c> — identical to
    /// the Watcher's <c>recording_key</c> so the ledger claim + state advance target the same row.
    /// </summary>
    public string IdempotencyKey => HasAudio ? $"rec:{Id}:{AudioSha256}" : $"rec:{Id}:txt:{GoogleTxtSha256}";
}
