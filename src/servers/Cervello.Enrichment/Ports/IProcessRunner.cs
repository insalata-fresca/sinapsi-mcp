namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for running an external subprocess and capturing its stdout/stderr, with an OPTIONAL
/// stdin payload. The only current caller is <see cref="Cervello.Enrichment.Pipeline.FfmpegAudioClipCutter"/>
/// (design <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7 phase V2, §1.4/§4.2): it
/// feeds a recording's audio bytes to <c>ffmpeg</c> on stdin and reads the cut clip back on stdout,
/// exactly mirroring how the CT139 diarize-embed sidecar itself hands raw audio bytes to ffmpeg
/// (<c>GatewayDiarizeEmbedClient</c> doc: "hands it straight to ffmpeg"). Kept as a seam (not a
/// direct <see cref="System.Diagnostics.Process"/> call inline) so the clip-cutter's argv
/// construction and stdin/stdout wiring are unit-testable against a fake runner, without spawning a
/// real ffmpeg binary in the test suite.
///
/// <para><b>Safety.</b> Implementations MUST launch the process with an argv list
/// (<c>ProcessStartInfo.ArgumentList</c>), never a shell-interpolated command string — no argument
/// is ever concatenated into a shell command line, so there is no shell-injection surface regardless
/// of what a caller passes as an argument value.</para>
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Run <paramref name="fileName"/> with <paramref name="arguments"/> (passed as an argv list,
    /// never shell-interpolated), optionally piping <paramref name="stdin"/> to the process's
    /// standard input, and returns the captured stdout bytes + stderr text + exit code. Bounded by
    /// <paramref name="ct"/>; implementations should also enforce an internal timeout ceiling and
    /// kill the process tree on cancellation/timeout.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte>? stdin = null,
        CancellationToken ct = default);
}

/// <summary>The captured result of an <see cref="IProcessRunner.RunAsync"/> call.</summary>
public sealed record ProcessResult(int ExitCode, byte[] Stdout, string Stderr, bool TimedOut);
