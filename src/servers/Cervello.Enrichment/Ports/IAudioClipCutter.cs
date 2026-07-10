using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for cutting a short representative audio clip out of a recording's staged audio (design
/// <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7 phase V2, §1.4/§4.2). The clip is
/// handed to a LATER phase (V3/V4) for upload to the operator's own Drive
/// <c>cervello/recordings/voiceprints/</c> — this port only produces the bytes; it never uploads,
/// never persists, and never lets audio leave CT146 (the recording bytes come from
/// <see cref="IAudioSource"/>, the on-CT staging blob store, and the returned clip bytes are handed
/// back in-process).
///
/// <para><b>Confinement:</b> the clip is cut FROM audio that already lives on CT146 (the operator's
/// own recording); the cutter itself performs no network I/O and touches no git working tree.</para>
/// </summary>
public interface IAudioClipCutter
{
    /// <summary>
    /// Cut a clip of at most ~<see cref="Pipeline.RepresentativeSegmentPicker.DefaultMaxWindowSeconds"/>
    /// seconds from <paramref name="window"/>'s <c>{Start,End}</c> range within the audio identified
    /// by <paramref name="audioFormat"/>/<paramref name="audioSha256"/> (resolved by the caller's
    /// <see cref="IAudioSource"/>; this port receives only the resolved bytes, never a raw filesystem
    /// path — there is no path/argument-injection surface). Returns the cut clip's bytes + the output
    /// container/mime the caller should upload as.
    /// </summary>
    Task<AudioClip> CutClipAsync(
        ReadOnlyMemory<byte> sourceAudio,
        string audioFormat,
        RepresentativeWindow window,
        CancellationToken ct = default);
}

/// <summary>A cut clip's bytes + its container format (design §7 V2 default: re-encoded <c>m4a</c> — see §9.5).</summary>
public sealed record AudioClip(byte[] Bytes, string Format)
{
    public byte[] Bytes { get; } = Bytes ?? throw new ArgumentNullException(nameof(Bytes));
    public string Format { get; } = string.IsNullOrWhiteSpace(Format)
        ? throw new ArgumentException("AudioClip.Format must be non-empty", nameof(Format))
        : Format;
}

/// <summary>
/// The clip cutter could not produce a clip — e.g. ffmpeg exited non-zero, or the source audio was
/// too short for the requested window. Terminal for THIS clip only (SCHEMAS §5 style): the caller
/// (V4 orchestration) treats it as "skip this voice's sample, do not fabricate a clip", never as a
/// reason to retry indefinitely.
/// </summary>
public sealed class AudioClipCutFailedException(string reason, Exception? inner = null)
    : Exception(reason, inner);
