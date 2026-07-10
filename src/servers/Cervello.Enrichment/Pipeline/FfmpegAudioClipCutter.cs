using System.Globalization;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Pipeline;

/// <summary>
/// Live <see cref="IAudioClipCutter"/> — shells out to the host <c>ffmpeg</c> binary (design
/// <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7 phase V2, §1.4/§4.2) to cut a
/// short representative clip out of a recording's audio. Runs on CT146 only (the enrichment host
/// image now ships <c>ffmpeg</c> — see the <c>Cervello.Enrichment.Host</c> Containerfile) so the
/// audio never has to transit to the CT139 diarize-embed sidecar just to be sliced (design §1.4:
/// "reuse the CT139 sidecar to cut — rejected … widening the audio's transit surface for no
/// benefit").
///
/// <para><b>No filesystem path, no shell — argv + stdin/stdout only.</b> This adapter NEVER writes
/// the source audio to a file and NEVER references a caller-supplied path: the resolved audio bytes
/// (already fetched via <see cref="IAudioSource"/> by the caller) are piped to ffmpeg on
/// <c>stdin</c> (<c>-i pipe:0</c>) and the cut clip is read back on <c>stdout</c>
/// (<c>-f &lt;container&gt; pipe:1</c>). Every argument is passed through
/// <see cref="IProcessRunner"/>'s <c>ArgumentList</c>-based launch (never a shell-interpolated
/// command string), so there is no shell-injection surface regardless of input; the only
/// caller-influenced argument values are numeric (<c>-ss</c>/<c>-t</c>, formatted with
/// <see cref="CultureInfo.InvariantCulture"/> from a validated <see cref="RepresentativeWindow"/>),
/// eliminating even an argument-splitting concern.</para>
///
/// <para><b>Re-encode, not stream-copy</b> (design §9.5: "Google Recorder exports .m4a; -c copy at
/// an arbitrary -ss may not land on a keyframe … re-encode the 30 s clip (cheap, correctness over
/// speed)"). Output is AAC in an <c>ipod</c> (m4a-compatible) container, muxed as FRAGMENTED MP4
/// (<c>-movflags frag_keyframe+empty_moov</c>) — the standard m4a muxer writes its moov atom up
/// front and seeks back to patch it, which a non-seekable <c>pipe:1</c> rejects outright
/// ("muxer does not support non seekable output", confirmed against a real ffmpeg binary); the
/// fragmented variant is a normal, player-compatible M4A that never needs to seek the output.</para>
/// </summary>
public sealed class FfmpegAudioClipCutter : IAudioClipCutter
{
    /// <summary>The ffmpeg binary — overridable for a non-standard PATH (matches the <c>StepCli</c> pattern).</summary>
    public const string DefaultFfmpegBin = "ffmpeg";

    /// <summary>
    /// Output container/format ffmpeg is told to mux to. <c>ipod</c> (the standard m4a muxer) needs
    /// a SEEKABLE output — it writes the moov atom up front, then back-patches it — which
    /// <c>pipe:1</c> is not, so it fails with "muxer does not support non seekable output". MP4/M4A
    /// support a streamable variant via <c>-movflags frag_keyframe+empty_moov</c> ("fragmented
    /// MP4": an empty moov atom up front + self-contained moof/mdat fragments after, valid M4A/MP4,
    /// playable by any modern player) — this keeps the §7 V2 "re-encode … m4a" contract while being
    /// pipe-safe.
    /// </summary>
    private const string OutputFormat = "ipod";

    /// <summary>Required alongside <see cref="OutputFormat"/> so the m4a muxer never needs to seek stdout.</summary>
    private const string FragmentedMp4MovFlags = "frag_keyframe+empty_moov";

    /// <summary>The <see cref="AudioClip.Format"/> value reported for the default output container.</summary>
    public const string OutputExtension = "m4a";

    private readonly IProcessRunner _runner;
    private readonly string _ffmpegBin;

    public FfmpegAudioClipCutter(IProcessRunner runner, string ffmpegBin = DefaultFfmpegBin)
    {
        ArgumentNullException.ThrowIfNull(runner);
        if (string.IsNullOrWhiteSpace(ffmpegBin))
            throw new ArgumentException("ffmpegBin must be non-empty", nameof(ffmpegBin));
        _runner = runner;
        _ffmpegBin = ffmpegBin;
    }

    public async Task<AudioClip> CutClipAsync(
        ReadOnlyMemory<byte> sourceAudio,
        string audioFormat,
        RepresentativeWindow window,
        CancellationToken ct = default)
    {
        if (sourceAudio.IsEmpty)
            throw new AudioClipCutFailedException("source audio is empty — nothing to cut a clip from");
        if (string.IsNullOrWhiteSpace(audioFormat))
            throw new ArgumentException("audioFormat must be non-empty", nameof(audioFormat));
        ArgumentNullException.ThrowIfNull(window);

        // ffmpeg needs an input-format hint when reading from a headerless-ish pipe for some
        // containers; m4a/mp4 boxes are self-describing enough that -f isn't required for decode,
        // but we pass it explicitly so an ambiguous/short pipe payload never gets silently
        // misdetected. Normalise the caller's format (e.g. "m4a") to ffmpeg's demuxer name.
        var inputFormat = NormalizeInputFormat(audioFormat);

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-f", inputFormat,
            "-i", "pipe:0",
            "-ss", window.Start.ToString("0.###", CultureInfo.InvariantCulture),
            "-t", window.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-vn",                 // audio only — never copy an embedded artwork/video stream
            "-map_metadata", "-1", // strip source metadata (no filename/device leakage into the clip)
            "-c:a", "aac",
            "-b:a", "96k",
            "-movflags", FragmentedMp4MovFlags, // pipe-safe: no seek-back for the moov atom
            "-f", OutputFormat,
            "pipe:1",
        };

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_ffmpegBin, args, sourceAudio, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AudioClipCutFailedException($"ffmpeg failed to start: {ex.Message}", ex);
        }

        if (result.TimedOut)
            throw new AudioClipCutFailedException("ffmpeg clip-cut timed out");
        if (result.ExitCode != 0)
            throw new AudioClipCutFailedException(
                $"ffmpeg exited {result.ExitCode} cutting [{window.Start:0.###},{window.End:0.###}] " +
                $"from a {sourceAudio.Length}-byte {audioFormat} source: {result.Stderr}");
        if (result.Stdout.Length == 0)
            throw new AudioClipCutFailedException(
                "ffmpeg exited 0 but produced no output bytes — the source may be shorter than the requested window");

        return new AudioClip(result.Stdout, OutputExtension);
    }

    /// <summary>Map a recording's audio format hint to ffmpeg's demuxer name for the <c>-f</c> input flag.</summary>
    private static string NormalizeInputFormat(string audioFormat) => audioFormat.Trim().ToLowerInvariant() switch
    {
        "m4a" or "mp4" or "aac" => "mov,mp4,m4a,3gp,3g2,mj2",
        "wav" => "wav",
        _ => audioFormat.Trim().ToLowerInvariant(),
    };
}
