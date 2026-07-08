namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for fetching a recording's TRANSIENT audio bytes from the CT staging blob store
/// (DESIGN §5.2; the "fetched from the CT staging blob store" every audio-consuming stage
/// comment already assumes). <see cref="Stages.BaseTranscribeStage"/> and
/// <see cref="Stages.DiarizeEmbedStage"/> both take <c>ReadOnlyMemory&lt;byte&gt; audio</c> —
/// this seam is what SUPPLIES it once, at the top of a recording's run, so the orchestrator can
/// thread the same bytes to both without either stage owning the fetch.
///
/// <para><b>Confinement.</b> The bytes are inference-only: fetched, handed to the transcribe /
/// diarize clients transiently, and never persisted git-side (the stages already guarantee this).
/// The live adapter reads the on-CT staging blob keyed by <c>rec:&lt;id&gt;:&lt;sha&gt;</c>; a
/// fake returns scripted synthetic bytes in tests (no personal audio). A missing/short blob is a
/// terminal contract violation the orchestrator maps to <c>failed_terminal</c>.</para>
/// </summary>
public interface IAudioSource
{
    /// <summary>
    /// Fetch the transient audio bytes for a recording. The returned buffer is used for inference
    /// only and never stored. Throws <see cref="AudioUnavailableException"/> when the blob is
    /// absent / unreadable (a terminal contract violation — the recording cannot be enriched).
    /// </summary>
    Task<ReadOnlyMemory<byte>> FetchAsync(string recordingId, string audioSha256, CancellationToken ct = default);
}

/// <summary>
/// The staging blob for a recording is absent or unreadable — a terminal contract violation
/// (SCHEMAS §5 <c>failed_terminal</c>): without audio there is nothing to transcribe/diarize, and
/// the engine NEVER fabricates a transcript or segments.
/// </summary>
public sealed class AudioUnavailableException(string reason, Exception? inner = null)
    : Exception(reason, inner);
