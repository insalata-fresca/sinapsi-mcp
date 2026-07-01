// ---------------------------------------------------------------------------
// SubprocessToolErrorTests — the LOAD-BEARING leg (mirrors StepCa.Mcp's
// SubprocessToolErrorTests). Proves the hardening paths actually FIRE at the tool
// level, not just in the isolated helper unit tests:
//
//   1. tool guards short-circuit BEFORE any subprocess is spawned — pointing the
//      backend at a nonexistent bundle, an invalid input returns/throws the
//      structured error without ever launching a process (runs everywhere, no node).
//   2. a failing backend that EMITS A SECRET on stderr -> the tool surfaces
//      [redacted], not the raw secret (drives the real GeminiCli through a tiny
//      node stub; skipped when node is absent, e.g. a CI SDK image).
//   3. the timeout path actually fires -> a stub that never exits is killed and the
//      result is flagged TimedOut (also node-gated).
//
// NUL inputs use the C# escape \0, never a literal NUL byte.
// ---------------------------------------------------------------------------
using System.Text.Json;
using Gemini.Mcp;
using Xunit;

namespace Gemini.Mcp.Tests;

public sealed class SubprocessToolErrorTests : IDisposable
{
    private readonly string _root;

    public SubprocessToolErrorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gemini-suberr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private GeminiConfig CfgWithBin(string geminiBin, int timeoutMs = 5_000)
    {
        var cfg = new GeminiConfig(
            OutputDir: Path.Combine(_root, "out"),
            SessionDir: Path.Combine(_root, "sessions"),
            TaskDir: Path.Combine(_root, "tasks"),
            GeminiBin: geminiBin,
            DefaultTimeoutMs: timeoutMs,
            ResearchTimeoutMs: timeoutMs);
        Directory.CreateDirectory(cfg.OutputDir);
        Directory.CreateDirectory(cfg.SessionDir);
        Directory.CreateDirectory(cfg.TaskDir);
        return cfg;
    }

    private static bool NodePresent => File.Exists("/usr/bin/node");

    /// <summary>Write a node stub bundle and return its path.</summary>
    private string WriteStub(string name, string js)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, js);
        return path;
    }

    // ── 1. tool guards short-circuit before any subprocess (node-independent) ─

    [Theory]
    [InlineData("")]
    [InlineData("bad\0prompt")]
    public async Task Ask_invalid_prompt_short_circuits_before_spawn(string badPrompt)
    {
        // Backend points at a nonexistent bundle: if the guard did NOT short-circuit,
        // the call would still fail — but with a "gemini exited"/launcher error, not
        // the validation message. Asserting the validation message proves no spawn.
        var cfg = CfgWithBin("/nonexistent/gemini.js");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AskTool.Ask(cfg, badPrompt));
        Assert.DoesNotContain("exited", ex.Message);
        Assert.DoesNotContain("launcher", ex.Message);
    }

    [Fact]
    public async Task AskWithFiles_leading_dash_file_short_circuits_before_spawn()
    {
        var cfg = CfgWithBin("/nonexistent/gemini.js");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AskWithFilesTool.AskWithFiles(cfg, "hi", new[] { "-rf" }));
        Assert.Contains("file_paths[0]", ex.Message);
    }

    [Fact]
    public async Task SessionResume_path_traversal_id_short_circuits_before_filesystem()
    {
        var cfg = CfgWithBin("/nonexistent/gemini.js");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SessionResumeTool.SessionResume(cfg, "../escape", "hi"));
        // Rejected as an invalid id, NOT as "session not found" (which would mean the
        // path was built and the filesystem consulted).
        Assert.DoesNotContain("not found", ex.Message);
    }

    [Fact]
    public void GetStatus_path_traversal_id_short_circuits_before_filesystem()
    {
        var cfg = CfgWithBin("/nonexistent/gemini.js");
        var ex = Assert.Throws<InvalidOperationException>(
            () => GetStatusTool.GetStatus(cfg, "../../etc/passwd"));
        Assert.DoesNotContain("not found", ex.Message);
    }

    [Fact]
    public void Research_invalid_depth_returns_structured_error_without_minting_a_task()
    {
        var cfg = CfgWithBin("/nonexistent/gemini.js");
        var before = Directory.GetFiles(cfg.TaskDir).Length;

        var json = ResearchTool.Research(cfg, "topic", depth: "shallow");
        var root = JsonDocument.Parse(json).RootElement;

        Assert.True(root.TryGetProperty("error", out var err));
        Assert.Contains("depth", err.GetString());
        // No running handle, no task file was written.
        Assert.False(root.TryGetProperty("task_id", out _));
        Assert.Equal(before, Directory.GetFiles(cfg.TaskDir).Length);
    }

    [Fact]
    public async Task ImageGenerate_invalid_aspect_ratio_short_circuits_before_making_a_call_dir()
    {
        var cfg = CfgWithBin("/nonexistent/gemini.js");
        var before = Directory.GetDirectories(cfg.OutputDir).Length;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ImageGenerateTool.ImageGenerate(cfg, "a cube", aspect_ratio: "2:1"));
        var err = JsonDocument.Parse(ex.Message).RootElement;
        Assert.Contains("aspect_ratio", err.GetProperty("error").GetString());
        // No per-call temp dir was created (the guard fired before Directory.CreateDirectory).
        Assert.Equal(before, Directory.GetDirectories(cfg.OutputDir).Length);
    }

    // ── 2. failing backend emits a secret -> tool returns [redacted] (node) ──

    [Fact]
    public async Task Ask_backendEmitsSecretOnStderr_isRedactedInTheError()
    {
        if (!NodePresent) return; // node absent (e.g. CI SDK image) — the redaction/timeout legs are node-gated

        // A stub bundle that writes a fake credential to stderr and exits non-zero.
        // Driven through the REAL GeminiCli (node <stub> <args>), so this exercises
        // the whole spawn -> capture -> AskToolShared.Failure -> GeminiErrors path.
        var stub = WriteStub("leak.js",
            "process.stderr.write('fatal: NANOBANANA_API_KEY=hunter2-supersecret\\n');\n" +
            "process.exit(3);\n");
        var cfg = CfgWithBin(stub);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AskTool.Ask(cfg, "hello"));

        // The diagnostic survives…
        Assert.Contains("fatal", ex.Message);
        // …but the secret value is redacted, and the exit code is surfaced.
        Assert.DoesNotContain("hunter2-supersecret", ex.Message);
        Assert.Contains("[redacted]", ex.Message);
        Assert.Contains("exited 3", ex.Message);
    }

    [Fact]
    public void Research_backendEmitsSecret_isRedactedInThePersistedTaskFile()
    {
        if (!NodePresent) return; // node absent (e.g. CI SDK image) — the redaction/timeout legs are node-gated

        var stub = WriteStub("leak2.js",
            "process.stderr.write('boom token=SECRETTOKENVALUE\\n');\n" +
            "process.exit(4);\n");
        var cfg = CfgWithBin(stub);

        var started = ResearchTool.Research(cfg, "topic", depth: "quick");
        var taskId = JsonDocument.Parse(started).RootElement.GetProperty("task_id").GetString()!;
        var taskFile = Path.Combine(cfg.TaskDir, $"{taskId}.json");

        // Wait for the fire-and-forget background run to complete and write back.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        string status = "running";
        while (DateTime.UtcNow < deadline)
        {
            var doc = JsonDocument.Parse(File.ReadAllText(taskFile)).RootElement;
            status = doc.GetProperty("status").GetString()!;
            if (status != "running") break;
            Thread.Sleep(200);
        }

        Assert.Equal("failed", status);
        var raw = File.ReadAllText(taskFile);
        Assert.DoesNotContain("SECRETTOKENVALUE", raw);
        Assert.Contains("[redacted]", raw);
    }

    // ── 3. the timeout path actually fires (node) ────────────────────────────

    [Fact]
    public async Task GeminiCli_timeout_killsTheProcess_andFlagsTimedOut()
    {
        if (!NodePresent) return; // node absent (e.g. CI SDK image) — the redaction/timeout legs are node-gated

        // A stub that never exits on its own; a short timeout must kill it.
        var stub = WriteStub("hang.js", "setInterval(() => {}, 1000);\n");
        var cfg = CfgWithBin(stub, timeoutMs: 800);

        var r = await GeminiCli.RunAsync(cfg, new[] { "-p", "x" }, cwd: _root);

        Assert.True(r.TimedOut);
        Assert.Null(r.ExitCode);
        Assert.Contains("killed after 800ms timeout", r.Stderr);
    }
}
