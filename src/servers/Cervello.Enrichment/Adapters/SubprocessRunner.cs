using System.Diagnostics;
using System.Text;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IProcessRunner"/> — spawns a subprocess via <see cref="ProcessStartInfo.ArgumentList"/>
/// (never a shell-interpolated command string, so there is no shell-injection surface), optionally
/// writes <c>stdin</c> bytes, and captures <c>stdout</c> as bytes (binary-safe — the ffmpeg caller
/// reads a cut audio clip back on stdout) and <c>stderr</c> as text (diagnostics only).
///
/// <para><b>Hardening (mirrors <c>StepCa.Mcp.StepCli</c>, the existing homelab pattern for a
/// redirected-pipe subprocess):</b> H3 — a synchronous <see cref="Process.WaitForExit()"/> after
/// <see cref="Process.WaitForExitAsync(CancellationToken)"/> to drain the async read handlers
/// (dotnet/runtime#42194); H5 — on timeout/cancellation, <see cref="Process.Kill(bool)"/> the whole
/// tree then a bounded wait so no ffmpeg descendant survives past the cancel. A
/// <see cref="TimeoutMs"/> ceiling always applies (default 30 s — a 30 s clip cut should never
/// legitimately run longer), linked with the caller's token.</para>
/// </summary>
public sealed class SubprocessRunner : IProcessRunner
{
    /// <summary>Default subprocess timeout ceiling — generous for a ~30s ffmpeg cut, bounded so a hung process is always reaped.</summary>
    public const int DefaultTimeoutMs = 30_000;

    private readonly int _timeoutMs;

    public SubprocessRunner(int timeoutMs = DefaultTimeoutMs)
    {
        if (timeoutMs <= 0)
            throw new ArgumentException("timeoutMs must be > 0", nameof(timeoutMs));
        _timeoutMs = timeoutMs;
    }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte>? stdin = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("fileName must be non-empty", nameof(fileName));
        ArgumentNullException.ThrowIfNull(arguments);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin.HasValue,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdoutBuf = new MemoryStream();
        var stderrBuf = new StringBuilder();

        p.Start();

        // Write stdin (if any) BEFORE awaiting stdout, then close it so ffmpeg sees EOF.
        // Read stdout concurrently via CopyToAsync (binary-safe, unlike line-buffered
        // OutputDataReceived, which would corrupt an audio payload at any embedded newline byte).
        //
        // A process that only needs a PREFIX of stdin (e.g. ffmpeg with -t cutting a short clip
        // out of a longer source) may finish reading and close its end of the pipe before this
        // write completes — that is an expected race, not a failure: an IOException/
        // ObjectDisposedException from the write or the subsequent Close() here just means "the
        // reader stopped listening", which is fine as long as the process itself still exits
        // cleanly (checked via ExitCode below, same as any other run).
        Task? stdinTask = null;
        if (stdin.HasValue)
        {
            var bytes = stdin.Value;
            stdinTask = Task.Run(async () =>
            {
                try
                {
                    await p.StandardInput.BaseStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                    p.StandardInput.Close();
                }
                catch (IOException)
                {
                    // Reader (ffmpeg) closed its end first — benign, see comment above.
                }
                catch (ObjectDisposedException)
                {
                    // Process/stream already torn down (exited very fast) — benign.
                }
            }, ct);
        }

        var stdoutTask = p.StandardOutput.BaseStream.CopyToAsync(stdoutBuf, ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = new CancellationTokenSource(_timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await p.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            // Drain the pipe-copy tasks now that the process has exited (H3 analogue for
            // stream-based redirection: ensure the reads have actually completed).
            await Task.WhenAll(
                stdoutTask,
                stderrTask,
                stdinTask ?? Task.CompletedTask).ConfigureAwait(false);
            p.WaitForExit();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            try
            {
                using var killTimeoutCts = new CancellationTokenSource(5000);
                await p.WaitForExitAsync(killTimeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* SIGKILL not enough — accept */ }
            catch { /* best-effort */ }

            stderrBuf.Append(stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "");
            return new ProcessResult(
                ExitCode: -1,
                Stdout: stdoutBuf.ToArray(),
                Stderr: $"{fileName} process killed after {_timeoutMs}ms timeout\n{stderrBuf}",
                TimedOut: true);
        }

        return new ProcessResult(
            ExitCode: p.ExitCode,
            Stdout: stdoutBuf.ToArray(),
            Stderr: stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "",
            TimedOut: false);
    }
}
