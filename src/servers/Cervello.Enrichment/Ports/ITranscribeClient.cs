namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for the OPTIONAL CT126-speaches base-transcription FALLBACK (spec <c>text-correction</c>).
/// The RATIFIED base is the Google <c>.txt</c> transcript (<see cref="IBaseTranscriptSource"/>), which
/// the engine keeps verbatim; it does NOT re-transcribe the audio from scratch as the base. This
/// client is used ONLY when a recording carries no Google <c>.txt</c> AND the operator has explicitly
/// enabled the fallback (<c>CERVELLO_BASE_RETRANSCRIBE_ENABLED=true</c>) — otherwise it is never wired
/// or called, so a full drain never depends on CT126 for the base. When used, the returned
/// <see cref="BaseTranscript"/> is the immutable substrate the (E4) correction pass diffs against — it
/// is never overwritten. A fake stands in for CT126 in tests (no live endpoint).
/// </summary>
public interface ITranscribeClient
{
    /// <param name="audio">Recording audio bytes — transient inference only.</param>
    /// <param name="format">Container hint (<c>m4a</c> | <c>wav</c>).</param>
    /// <param name="language">Correct-language config (e.g. <c>fr</c>, <c>en</c>).</param>
    Task<BaseTranscript> TranscribeAsync(
        ReadOnlyMemory<byte> audio,
        string format,
        string language,
        CancellationToken ct = default);
}

/// <summary>
/// The base transcript produced by CT126: markdown body + the language it was transcribed
/// in. This is the substrate; corrections (E4) are diffs against <see cref="Markdown"/>.
/// </summary>
public sealed record BaseTranscript
{
    public BaseTranscript(string markdown, string language)
    {
        // A base transcript may legitimately be empty text (silent recording), but the
        // markdown container is non-null and the language is required.
        ArgumentNullException.ThrowIfNull(markdown);
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("BaseTranscript.Language must be non-empty", nameof(language));
        Markdown = markdown;
        Language = language;
    }

    public string Markdown { get; }
    public string Language { get; }
}
