using System.Diagnostics;
using System.Text;

namespace StepCa.Mcp;

/// <summary>
/// Subprocess wrapper around the host-installed <c>step</c> CLI. The hardening
/// patterns matter for any redirected-pipe subprocess:
///   H3 — a synchronous <c>WaitForExit()</c> after <c>WaitForExitAsync(token)</c>
///        to drain the <c>BeginOutput/ErrorReadLine</c> pipes (dotnet/runtime#42194).
///   H5 — a bounded <c>WaitForExitAsync(5s)</c> after <c>Process.Kill</c> so
///        descendants do not survive past the cancel.
/// A linked CTS applies the <c>STEP_SUBPROCESS_TIMEOUT_MS</c> ceiling (default
/// 30 s); <c>STEP_CA_HTTP_TIMEOUT_MS</c> is honoured as a back-compat alias.
/// </summary>
public sealed class StepCli
{
    private readonly StepCaOptions _opts;

    public StepCli(StepCaOptions opts) => _opts = opts;

    public async Task<StepResult> RunAsync(string[] args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _opts.StepBin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(_opts.SubprocessTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await p.WaitForExitAsync(linkedCts.Token);
            // H3 (dotnet/runtime#42194): drain async output handlers.
            p.WaitForExit();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // H5: kill tree + bounded wait for descendants.
            try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            try
            {
                using var killTimeoutCts = new CancellationTokenSource(5000);
                await p.WaitForExitAsync(killTimeoutCts.Token);
            }
            catch (OperationCanceledException) { /* SIGKILL not enough — accept */ }
            catch { /* best-effort */ }
            return new StepResult(
                ExitCode: -1,
                Stdout:   stdout.ToString(),
                Stderr:   $"step process killed after {_opts.SubprocessTimeoutMs}ms timeout\n" + stderr,
                TimedOut: true);
        }

        return new StepResult(
            ExitCode: p.ExitCode,
            Stdout:   stdout.ToString(),
            Stderr:   stderr.ToString(),
            TimedOut: false);
    }
}

public sealed record StepResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);
