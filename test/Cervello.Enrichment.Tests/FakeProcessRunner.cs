using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Scripted <see cref="IProcessRunner"/> for unit-testing <see cref="Cervello.Enrichment.Pipeline.FfmpegAudioClipCutter"/>
/// WITHOUT spawning a real ffmpeg binary. Captures every call's file name / argv / stdin so a test
/// can assert the exact command line the cutter constructs, and returns a scripted
/// <see cref="ProcessResult"/> (defaulting to exit 0 + an echo of the stdin bytes, which is close
/// enough to "ffmpeg cut a clip" for argv-construction tests that don't care about real audio bytes).
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    public sealed record Call(string FileName, IReadOnlyList<string> Arguments, ReadOnlyMemory<byte>? Stdin);

    public List<Call> Calls { get; } = [];

    /// <summary>When set, returned verbatim instead of the default echo-stdin result.</summary>
    public ProcessResult? ScriptedResult { get; set; }

    /// <summary>When set, thrown instead of returning (simulates "ffmpeg binary missing" / OS-level failure).</summary>
    public Exception? ThrowOnRun { get; set; }

    public Task<ProcessResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, ReadOnlyMemory<byte>? stdin = null, CancellationToken ct = default)
    {
        Calls.Add(new Call(fileName, arguments, stdin));

        if (ThrowOnRun is not null) throw ThrowOnRun;

        if (ScriptedResult is not null) return Task.FromResult(ScriptedResult);

        // Default: pretend ffmpeg "cut" a clip by echoing the stdin bytes back — enough to assert
        // the cutter propagates ffmpeg's stdout into the AudioClip without inventing bytes.
        var stdout = stdin?.ToArray() ?? [];
        return Task.FromResult(new ProcessResult(ExitCode: 0, Stdout: stdout, Stderr: "", TimedOut: false));
    }
}
