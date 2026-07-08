using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline.Stages;

/// <summary>
/// Produces the base transcript (spec <c>text-correction</c> → "Base transcript is the
/// correction substrate"). Re-transcribes the recording via CT126 (<see cref="ITranscribeClient"/>,
/// correct-language config) and persists it at <c>recordings/transcripts/&lt;id&gt;.md</c> via
/// <see cref="ITranscriptStore"/> BEFORE any correction diff is computed. The base is written
/// once; the (E4) correction stage never overwrites it. Audio bytes are fetched transiently
/// and passed straight to the transcribe client — never persisted git-side.
/// </summary>
public sealed class BaseTranscribeStage(
    ITranscribeClient transcribeClient,
    ITranscriptStore transcriptStore,
    ILogger<BaseTranscribeStage>? logger = null)
{
    private readonly ITranscribeClient _transcribe =
        transcribeClient ?? throw new ArgumentNullException(nameof(transcribeClient));
    private readonly ITranscriptStore _store =
        transcriptStore ?? throw new ArgumentNullException(nameof(transcriptStore));
    private readonly ILogger _log = logger ?? NullLogger<BaseTranscribeStage>.Instance;

    /// <summary>
    /// Transcribe and persist the base transcript for a recording. Returns the base transcript
    /// and the repo-relative path it was written at. Idempotent: if a base already exists, the
    /// existing one is returned and NOT overwritten.
    /// </summary>
    /// <param name="audio">Transient audio bytes (fetched from the CT staging blob store).</param>
    public async Task<BaseTranscribeResult> TranscribeAsync(
        RecordingRef recording,
        ReadOnlyMemory<byte> audio,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recording);

        var path = _store.TranscriptPath(recording.Id);

        if (await _store.ExistsAsync(recording.Id, ct).ConfigureAwait(false))
        {
            _log.LogInformation("base-transcribe no-op {Id}: transcript already exists at {Path}",
                recording.Id, path);
            return new BaseTranscribeResult(path, Transcript: null, AlreadyExisted: true);
        }

        if (audio.IsEmpty)
            throw new ArgumentException("base-transcribe requires non-empty audio bytes", nameof(audio));

        var transcript = await _transcribe
            .TranscribeAsync(audio, recording.Format, recording.Language, ct)
            .ConfigureAwait(false);

        var written = await _store.WriteBaseAsync(recording.Id, transcript, ct).ConfigureAwait(false);
        _log.LogInformation("base-transcribe wrote {Id} → {Path} (lang {Lang})",
            recording.Id, written, transcript.Language);

        return new BaseTranscribeResult(written, transcript, AlreadyExisted: false);
    }
}

/// <summary>Outcome of <see cref="BaseTranscribeStage.TranscribeAsync"/>.</summary>
public sealed record BaseTranscribeResult(string TranscriptPath, BaseTranscript? Transcript, bool AlreadyExisted);
