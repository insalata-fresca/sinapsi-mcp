using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port that supplies the RATIFIED base transcript for a recording: the Google Recorder
/// <c>.txt</c> transcript the Watcher paired by basename and staged on-CT (manifest §8
/// <c>google_txt</c>). The ratified design (operator, explicit): <b>the Google <c>.txt</c> IS the
/// base</b> — kept verbatim and enhanced only by evidence-gated correction diffs downstream. The
/// engine does NOT re-transcribe the audio from scratch to produce the base (explicitly rejected as
/// costly and no better than Google); CT126 is reserved for SELECTIVE re-ASR of garbled spans only.
///
/// <para><b>Never-guess floor.</b> The returned base is the Google text VERBATIM (only wrapped in the
/// <see cref="BaseTranscript"/> container in the recording's language). It is never paraphrased,
/// summarised, or fabricated. When no Google <c>.txt</c> is present the source returns
/// <see langword="null"/> — the caller decides how to proceed (per config), never invents a base.</para>
///
/// <para>The live adapter reads the staged <c>.txt</c> bytes from the CT staging blob store the
/// Watcher writes (content-addressed, transient, never git-side); a fake returns scripted text in
/// tests. A missing / unreadable staged blob is treated as "no Google base present" (returns
/// <see langword="null"/>), not a hard failure — the graceful-degrade posture.</para>
/// </summary>
public interface IBaseTranscriptSource
{
    /// <summary>
    /// Return the Google <c>.txt</c> base transcript for a recording, or <see langword="null"/> when
    /// no Google transcript is present / locatable for it. Never throws for an absent base (a missing
    /// Google <c>.txt</c> is a normal, gracefully-handled case), and never fabricates text.
    /// </summary>
    /// <param name="recording">
    /// The recording handle — carries the id, the recording language (wraps the verbatim Google text),
    /// and the OPTIONAL <see cref="RecordingRef.GoogleTxtSha256"/> the live adapter uses to locate the
    /// staged <c>.txt</c> blob. A <see langword="null"/> sha ⇒ no Google transcript ⇒ returns null.
    /// </param>
    Task<BaseTranscript?> GetGoogleBaseAsync(RecordingRef recording, CancellationToken ct = default);
}
