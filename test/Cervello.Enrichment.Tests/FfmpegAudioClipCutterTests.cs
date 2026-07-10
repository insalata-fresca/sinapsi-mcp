using System.Text;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Voiceprint-naming V2 — <see cref="FfmpegAudioClipCutter"/> (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V2, §1.4/§4.2). Proves, against a
/// <see cref="FakeProcessRunner"/> (no real ffmpeg spawned): the argv is constructed correctly
/// (input format, -ss/-t from the window, output container, no filesystem path anywhere); the
/// source audio is piped on stdin (never written to disk / never referenced by path); a non-zero
/// ffmpeg exit surfaces as <see cref="AudioClipCutFailedException"/>; empty stdout (source shorter
/// than the window) surfaces as a failure rather than an empty clip; a short/empty source audio
/// input is rejected before ever invoking the runner; the returned <see cref="AudioClip"/> carries
/// the runner's stdout bytes verbatim.
/// </summary>
public sealed class FfmpegAudioClipCutterTests
{
    private static readonly byte[] FakeAudioBytes = Encoding.UTF8.GetBytes("not real audio, just test bytes");

    [Fact] // scenario: argv is built correctly — ss/t from the window, no filesystem path, pipe:0/pipe:1
    public async Task Constructs_argv_with_window_bounds_and_no_filesystem_path()
    {
        var runner = new FakeProcessRunner();
        var cutter = new FfmpegAudioClipCutter(runner);
        var window = new RepresentativeWindow("rec-1", 12.5, 40.25);

        await cutter.CutClipAsync(FakeAudioBytes, "m4a", window);

        Assert.Single(runner.Calls);
        var call = runner.Calls[0];
        Assert.Equal("ffmpeg", call.FileName);

        var args = call.Arguments;
        Assert.Contains("pipe:0", args);
        Assert.Contains("pipe:1", args);
        // -ss <start>
        var ssIdx = args.ToList().IndexOf("-ss");
        Assert.True(ssIdx >= 0 && ssIdx + 1 < args.Count);
        Assert.Equal("12.5", args[ssIdx + 1]);
        // -t <duration>, NOT the end time
        var tIdx = args.ToList().IndexOf("-t");
        Assert.True(tIdx >= 0 && tIdx + 1 < args.Count);
        Assert.Equal("27.75", args[tIdx + 1]); // 40.25 - 12.5

        // No argument anywhere looks like a filesystem path (no leading '/', no '.m4a'/'.wav' file path).
        Assert.DoesNotContain(args, a => a.StartsWith('/') || a.Contains("staging"));

        // Pipe-safe m4a mux: fragmented MP4 flags MUST be present (the plain 'ipod' muxer rejects a
        // non-seekable pipe:1 output — see the type doc / real-ffmpeg-verified comment).
        var movflagsIdx = args.ToList().IndexOf("-movflags");
        Assert.True(movflagsIdx >= 0 && movflagsIdx + 1 < args.Count);
        Assert.Equal("frag_keyframe+empty_moov", args[movflagsIdx + 1]);
    }

    [Fact] // scenario: source audio bytes are piped on stdin, never written to a file
    public async Task Pipes_source_audio_on_stdin()
    {
        var runner = new FakeProcessRunner();
        var cutter = new FfmpegAudioClipCutter(runner);

        await cutter.CutClipAsync(FakeAudioBytes, "m4a", new RepresentativeWindow("rec-1", 0, 10));

        Assert.Single(runner.Calls);
        Assert.True(runner.Calls[0].Stdin.HasValue);
        Assert.Equal(FakeAudioBytes, runner.Calls[0].Stdin!.Value.ToArray());
    }

    [Theory] // scenario: input format hint maps to ffmpeg's demuxer name for the -f flag
    [InlineData("m4a", "mov,mp4,m4a,3gp,3g2,mj2")]
    [InlineData("wav", "wav")]
    [InlineData("MP4", "mov,mp4,m4a,3gp,3g2,mj2")]
    public async Task Normalises_input_format(string inputHint, string expectedDemuxer)
    {
        var runner = new FakeProcessRunner();
        var cutter = new FfmpegAudioClipCutter(runner);

        await cutter.CutClipAsync(FakeAudioBytes, inputHint, new RepresentativeWindow("rec-1", 0, 5));

        var args = runner.Calls[0].Arguments.ToList();
        var firstDashF = args.IndexOf("-f");
        Assert.Equal(expectedDemuxer, args[firstDashF + 1]);
    }

    [Fact] // scenario: ffmpeg succeeds — returns the stdout bytes as the clip, format = m4a
    public async Task Returns_clip_bytes_and_format_on_success()
    {
        var runner = new FakeProcessRunner
        {
            ScriptedResult = new ProcessResult(ExitCode: 0, Stdout: [1, 2, 3, 4], Stderr: "", TimedOut: false),
        };
        var cutter = new FfmpegAudioClipCutter(runner);

        var clip = await cutter.CutClipAsync(FakeAudioBytes, "m4a", new RepresentativeWindow("rec-1", 0, 30));

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, clip.Bytes);
        Assert.Equal("m4a", clip.Format);
    }

    [Fact] // scenario: ffmpeg exits non-zero — surfaced as AudioClipCutFailedException, never a fabricated clip
    public async Task NonZeroExit_throws_AudioClipCutFailedException()
    {
        var runner = new FakeProcessRunner
        {
            ScriptedResult = new ProcessResult(ExitCode: 1, Stdout: [], Stderr: "Invalid data found when processing input", TimedOut: false),
        };
        var cutter = new FfmpegAudioClipCutter(runner);

        var ex = await Assert.ThrowsAsync<AudioClipCutFailedException>(() =>
            cutter.CutClipAsync(FakeAudioBytes, "m4a", new RepresentativeWindow("rec-1", 0, 30)));
        Assert.Contains("Invalid data", ex.Message);
    }

    [Fact] // scenario: ffmpeg exits 0 but produces nothing (source shorter than the requested window) — a failure, not an empty clip
    public async Task ZeroExitButEmptyStdout_throws()
    {
        var runner = new FakeProcessRunner
        {
            ScriptedResult = new ProcessResult(ExitCode: 0, Stdout: [], Stderr: "", TimedOut: false),
        };
        var cutter = new FfmpegAudioClipCutter(runner);

        await Assert.ThrowsAsync<AudioClipCutFailedException>(() =>
            cutter.CutClipAsync(FakeAudioBytes, "m4a", new RepresentativeWindow("rec-1", 0, 30)));
    }

    [Fact] // scenario: timeout — surfaced as a failure
    public async Task TimedOut_throws()
    {
        var runner = new FakeProcessRunner
        {
            ScriptedResult = new ProcessResult(ExitCode: -1, Stdout: [], Stderr: "killed", TimedOut: true),
        };
        var cutter = new FfmpegAudioClipCutter(runner);

        var ex = await Assert.ThrowsAsync<AudioClipCutFailedException>(() =>
            cutter.CutClipAsync(FakeAudioBytes, "m4a", new RepresentativeWindow("rec-1", 0, 30)));
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // scenario: the process fails to even start (binary missing) — surfaced, never silently swallowed
    public async Task RunnerThrows_surfacesAsClipCutFailure()
    {
        var runner = new FakeProcessRunner { ThrowOnRun = new System.ComponentModel.Win32Exception("No such file or directory") };
        var cutter = new FfmpegAudioClipCutter(runner);

        var ex = await Assert.ThrowsAsync<AudioClipCutFailedException>(() =>
            cutter.CutClipAsync(FakeAudioBytes, "m4a", new RepresentativeWindow("rec-1", 0, 30)));
        Assert.Contains("ffmpeg failed to start", ex.Message);
    }

    [Fact] // scenario: short-recording edge case — empty source audio is rejected before invoking the runner at all
    public async Task EmptySourceAudio_rejectedWithoutInvokingRunner()
    {
        var runner = new FakeProcessRunner();
        var cutter = new FfmpegAudioClipCutter(runner);

        await Assert.ThrowsAsync<AudioClipCutFailedException>(() =>
            cutter.CutClipAsync(ReadOnlyMemory<byte>.Empty, "m4a", new RepresentativeWindow("rec-1", 0, 30)));

        Assert.Empty(runner.Calls); // never shells out for empty input
    }

    [Fact]
    public void Ctor_rejects_null_runner_and_blank_ffmpegBin()
    {
        Assert.Throws<ArgumentNullException>(() => new FfmpegAudioClipCutter(null!));
        Assert.Throws<ArgumentException>(() => new FfmpegAudioClipCutter(new FakeProcessRunner(), " "));
    }
}
