using System.Diagnostics;
using System.Text;

namespace Gemini.Mcp;

/// <summary>
/// Subprocess wrapper around the <c>gemini</c> CLI: spawn, capture stdout+stderr,
/// kill the process tree on timeout (SIGTERM then SIGKILL), and return a
/// <see cref="GeminiResult"/>.
/// </summary>
public static class GeminiCli
{
    public static async Task<GeminiResult> RunAsync(
        GeminiConfig cfg,
        string[] args,
        string cwd,
        int? timeoutMsOverride = null,
        CancellationToken ct = default)
    {
        // Invoke node directly with the gemini-cli bundle path as the first arg,
        // rather than the wrapper symlink: when the package directory is bind-mounted
        // into a container the symlink can resolve to its target and lose the package
        // context, so Node fails to resolve the bundle's relative chunk imports. Driving
        // `node <bundle.js>` keeps the working directory inside the package tree.
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/node",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(cfg.GeminiBin); // path to bundle/gemini.js
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // The launcher binary (cfg.GeminiBin's runtime, e.g. node) could not be
            // started: it is absent, not executable, or not on PATH. Surface this the
            // same way a CLI that started and failed is surfaced — no stdout, non-zero
            // exit, the OS error on stderr — so every caller's existing "exit != 0 / no
            // output" handling runs (the image tools hit their structured no-output
            // branch; ask/describe throw their "gemini exited" error). This also keeps
            // the error paths hermetic: they no longer depend on a launcher binary being
            // present in the environment (e.g. the CI SDK image has no node).
            return new GeminiResult(
                Stdout: string.Empty,
                Stderr: $"failed to start gemini launcher: {ex.Message}",
                ExitCode: ex.NativeErrorCode != 0 ? ex.NativeErrorCode : -1,
                TimedOut: false);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var effectiveTimeoutMs = timeoutMsOverride ?? cfg.DefaultTimeoutMs;
        using var timeoutCts = new CancellationTokenSource(effectiveTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await p.WaitForExitAsync(linkedCts.Token);
            // WaitForExitAsync returns once the process exits, but the async
            // BeginOutput/ErrorReadLine handlers may still have unflushed bytes in
            // their pipes; the synchronous WaitForExit() (no overload) drains them.
            // Without it the returned stdout/stderr can be truncated, and on a
            // Node.js subprocess disposal can block on the missing FD-close
            // handshake and appear as a hang.
            p.WaitForExit();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // SIGTERM the whole tree on timeout. Process.Kill(entireProcessTree:true)
            // does not wait for descendants, and Node.js can spawn its own children
            // (module loader, token-refresh worker, image extension) that may survive
            // the first signal walk and keep state-file FDs open — so we then wait
            // (bounded) for the tree to actually exit before returning.
            try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            try
            {
                using var killTimeoutCts = new CancellationTokenSource(5000);
                await p.WaitForExitAsync(killTimeoutCts.Token);
            }
            catch (OperationCanceledException) { /* SIGKILL not enough; accept + report via stderr */ }
            catch { /* best-effort */ }
            return new GeminiResult(
                Stdout: stdout.ToString(),
                Stderr: $"gemini process killed after {effectiveTimeoutMs}ms timeout\n" + stderr,
                ExitCode: null,
                TimedOut: true);
        }

        return new GeminiResult(
            Stdout: stdout.ToString(),
            Stderr: stderr.ToString(),
            ExitCode: p.ExitCode,
            TimedOut: false);
    }
}

public sealed record GeminiResult(string Stdout, string Stderr, int? ExitCode, bool TimedOut);
