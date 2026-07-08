namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for base transcription via CT126 speaches (spec <c>text-correction</c> →
/// "Base transcript is the correction substrate"). The engine re-transcribes the recording
/// audio in the correct language; the returned <see cref="BaseTranscript"/> is the immutable
/// substrate that the (later, E4) correction pass diffs against — it is never overwritten.
/// A fake stands in for CT126 in tests (no live endpoint).
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
