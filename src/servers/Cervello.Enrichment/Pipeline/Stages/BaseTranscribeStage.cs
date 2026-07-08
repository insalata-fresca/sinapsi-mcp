using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline.Stages;

/// <summary>
/// Produces the base transcript (spec <c>text-correction</c> → "Base transcript is the correction
/// substrate") and persists it at <c>recordings/transcripts/&lt;id&gt;.md</c> via
/// <see cref="ITranscriptStore"/> BEFORE any correction diff is computed. The base is written once;
/// the (E4) correction stage never overwrites it.
///
/// <para><b>The RATIFIED base is the Google <c>.txt</c> transcript</b> (operator decision, explicit +
/// repeated): the Watcher paired the Google Recorder transcript with the audio by basename and staged
/// it on-CT (manifest §8 <c>google_txt</c>). This stage reads that Google text via
/// <see cref="IBaseTranscriptSource"/> and persists it VERBATIM as the base — it does NOT re-transcribe
/// the audio from scratch (explicitly rejected as costly and no better than Google). CT126 base
/// re-transcription is an OPTIONAL fallback, OFF by default (<see cref="ITranscribeClient"/> is
/// injected only when the host enables it), used ONLY when a recording carries no Google <c>.txt</c>.
/// When there is no Google base and no enabled fallback, the stage returns
/// <see cref="BaseTranscribeResult.NoBase"/> — it never fabricates a transcript.</para>
///
/// <para><b>CT126 is not a hard dependency of a Google-base drain.</b> When a Google <c>.txt</c> is
/// present the transcribe client is never called, so a full drain of a real recording (which carries
/// a Google transcript) completes with CT126 unreachable. Selective re-ASR of garbled spans (also
/// CT126) is a separate, likewise-optional later enhancement handled in the correction stage.</para>
/// </summary>
public sealed class BaseTranscribeStage
{
    private readonly IBaseTranscriptSource _googleBase;
    private readonly ITranscriptStore _store;
    private readonly ITranscribeClient? _reTranscribe;
    private readonly bool _reTranscribeEnabled;
    private readonly ILogger _log;

    /// <summary>
    /// Construct the stage. <paramref name="reTranscribe"/> is the OPTIONAL CT126 re-transcription
    /// fallback: pass <see langword="null"/> (the default in the ratified posture) to disable it
    /// entirely, so the base is the Google <c>.txt</c> or nothing (a full drain never depends on
    /// CT126 for the base). When supplied AND <paramref name="reTranscribeEnabled"/> is true, it is
    /// used only for recordings that carry no Google <c>.txt</c>.
    /// </summary>
    public BaseTranscribeStage(
        IBaseTranscriptSource googleBase,
        ITranscriptStore transcriptStore,
        ITranscribeClient? reTranscribe = null,
        bool reTranscribeEnabled = false,
        ILogger<BaseTranscribeStage>? logger = null)
    {
        _googleBase = googleBase ?? throw new ArgumentNullException(nameof(googleBase));
        _store = transcriptStore ?? throw new ArgumentNullException(nameof(transcriptStore));
        _reTranscribe = reTranscribe;
        // The fallback is active only if BOTH a client is wired AND the config flag is on.
        _reTranscribeEnabled = reTranscribeEnabled && reTranscribe is not null;
        _log = logger ?? NullLogger<BaseTranscribeStage>.Instance;
    }

    /// <summary>
    /// Resolve and persist the base transcript for a recording. Prefers the Google <c>.txt</c> base;
    /// falls back to CT126 re-transcription ONLY when no Google base exists and the fallback is
    /// enabled. Idempotent: if a base already exists it is returned and NOT overwritten.
    /// </summary>
    /// <param name="recording">The recording being enriched (id + language).</param>
    /// <param name="audio">
    /// Transient audio bytes (fetched from the CT staging blob store) — used ONLY by the optional
    /// CT126 re-transcription fallback. When the Google base is present these are never touched here.
    /// </param>
    public async Task<BaseTranscribeResult> TranscribeAsync(
        RecordingRef recording,
        ReadOnlyMemory<byte> audio,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recording);

        var path = _store.TranscriptPath(recording.Id);

        if (await _store.ExistsAsync(recording.Id, ct).ConfigureAwait(false))
        {
            _log.LogInformation("base no-op {Id}: transcript already exists at {Path}",
                recording.Id, path);
            return new BaseTranscribeResult(path, Transcript: null, BaseSource.AlreadyExists);
        }

        // ── 1. RATIFIED PATH: the Google .txt IS the base (verbatim). CT126 is not consulted. ──────
        var google = await _googleBase
            .GetGoogleBaseAsync(recording, ct)
            .ConfigureAwait(false);
        if (google is not null)
        {
            var writtenPath = await _store.WriteBaseAsync(recording.Id, google, ct).ConfigureAwait(false);
            _log.LogInformation("base {Id} → {Path}: Google .txt is the base (verbatim, lang {Lang}); CT126 not called",
                recording.Id, writtenPath, google.Language);
            return new BaseTranscribeResult(writtenPath, google, BaseSource.GoogleTxt);
        }

        // ── 2. OPTIONAL FALLBACK: CT126 re-transcription — ONLY if explicitly enabled + wired. ────
        //     Default posture: disabled → we NEVER re-transcribe from scratch, never depend on CT126
        //     for the base. A recording with no Google .txt yields NoBase (handled by the pipeline).
        if (!_reTranscribeEnabled)
        {
            _log.LogWarning("base {Id}: no Google .txt and CT126 re-transcription is disabled — no base produced",
                recording.Id);
            return new BaseTranscribeResult(path, Transcript: null, BaseSource.NoBase);
        }

        if (audio.IsEmpty)
        {
            // The fallback needs audio; without it there is nothing to transcribe (never fabricate).
            _log.LogWarning("base {Id}: no Google .txt, fallback enabled, but audio is empty — no base produced",
                recording.Id);
            return new BaseTranscribeResult(path, Transcript: null, BaseSource.NoBase);
        }

        var transcript = await _reTranscribe!
            .TranscribeAsync(audio, recording.Format, recording.Language, ct)
            .ConfigureAwait(false);
        var written = await _store.WriteBaseAsync(recording.Id, transcript, ct).ConfigureAwait(false);
        _log.LogInformation("base {Id} → {Path}: CT126 re-transcription fallback (no Google .txt; lang {Lang})",
            recording.Id, written, transcript.Language);
        return new BaseTranscribeResult(written, transcript, BaseSource.Ct126Fallback);
    }
}

/// <summary>Where the base transcript came from (or why none was produced).</summary>
public enum BaseSource
{
    /// <summary>The ratified path: the Google <c>.txt</c> transcript, taken verbatim.</summary>
    GoogleTxt,

    /// <summary>The optional CT126 re-transcription fallback (only when enabled + no Google base).</summary>
    Ct126Fallback,

    /// <summary>A base already existed and was returned unchanged (idempotent re-run).</summary>
    AlreadyExists,

    /// <summary>No Google base, and no enabled fallback (or no audio) — no base produced (never fabricated).</summary>
    NoBase,
}

/// <summary>Outcome of <see cref="BaseTranscribeStage.TranscribeAsync"/>.</summary>
public sealed record BaseTranscribeResult(string TranscriptPath, BaseTranscript? Transcript, BaseSource Source)
{
    /// <summary>True iff a base transcript is available (Google, fallback, or a pre-existing one).</summary>
    public bool HasBase => Source is not BaseSource.NoBase;

    /// <summary>True iff a pre-existing base was returned unchanged.</summary>
    public bool AlreadyExisted => Source is BaseSource.AlreadyExists;
}
