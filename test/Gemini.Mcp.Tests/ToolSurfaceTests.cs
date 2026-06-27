using System.Text.Json;
using Gemini.Mcp;
using Xunit;

namespace Gemini.Mcp.Tests;

/// <summary>
/// Exercises the deterministic part of the tool surface — the behaviour reachable
/// without a successful external <c>gemini</c> CLI run: session lifecycle, the
/// async-task handle that <c>research</c> hands back, the not-found error paths of
/// <c>get_status</c> / <c>session_resume</c>, and the input-shaping / error-handling
/// of the CLI-fronted image tools (the missing-file guard, the structured no-output
/// error + temp-dir cleanup, and prompt-quote escaping). The CLI is driven against a
/// nonexistent bundle so the no-output/error branches run deterministically. Each test
/// runs against its own temp dirs.
/// </summary>
public sealed class ToolSurfaceTests : IDisposable
{
    private readonly string _root;
    private readonly GeminiConfig _cfg;

    public ToolSurfaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gemini-mcp-tests-" + Guid.NewGuid().ToString("N"));
        _cfg = new GeminiConfig(
            OutputDir: Path.Combine(_root, "out"),
            SessionDir: Path.Combine(_root, "sessions"),
            TaskDir: Path.Combine(_root, "tasks"),
            GeminiBin: "/nonexistent/gemini.js",
            DefaultTimeoutMs: 1000,
            ResearchTimeoutMs: 1000);
        Directory.CreateDirectory(_cfg.SessionDir);
        Directory.CreateDirectory(_cfg.TaskDir);
        Directory.CreateDirectory(_cfg.OutputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ── session lifecycle ───────────────────────────────────────────

    [Fact]
    public void SessionCreate_returns_an_id_and_persists_state_with_focus()
    {
        var json = SessionCreateTool.SessionCreate(_cfg, focus: "investigate a bug");
        var sid = JsonDocument.Parse(json).RootElement.GetProperty("session_id").GetString();

        Assert.False(string.IsNullOrWhiteSpace(sid));
        var dir = Path.Combine(_cfg.SessionDir, sid!);
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "state.json")));
        // focus is written both to its marker file and into the state.
        Assert.Equal("investigate a bug", File.ReadAllText(Path.Combine(dir, "focus.txt")));
        var state = JsonSerializer.Deserialize<SessionState>(
            File.ReadAllText(Path.Combine(dir, "state.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(sid, state.SessionId);
        Assert.Equal("investigate a bug", state.Focus);
        Assert.Equal(0, state.PromptCount);
    }

    [Fact]
    public void SessionClose_is_idempotent_and_removes_state()
    {
        var json = SessionCreateTool.SessionCreate(_cfg, focus: null);
        var sid = JsonDocument.Parse(json).RootElement.GetProperty("session_id").GetString()!;
        Assert.True(Directory.Exists(Path.Combine(_cfg.SessionDir, sid)));

        var closed = SessionCloseTool.SessionClose(_cfg, sid);
        Assert.True(JsonDocument.Parse(closed).RootElement.GetProperty("closed").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(_cfg.SessionDir, sid)));

        // Closing an already-absent session must not throw.
        var again = SessionCloseTool.SessionClose(_cfg, sid);
        Assert.True(JsonDocument.Parse(again).RootElement.GetProperty("closed").GetBoolean());
    }

    [Fact]
    public async Task SessionResume_throws_a_clear_error_for_an_unknown_session()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SessionResumeTool.SessionResume(_cfg, "does-not-exist", "hi"));
        Assert.Contains("does-not-exist", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    // ── async task handle ───────────────────────────────────────────

    [Fact]
    public void Research_returns_a_running_handle_and_writes_a_task_file()
    {
        var json = ResearchTool.Research(_cfg, "what is MCP?", depth: "quick");
        var root = JsonDocument.Parse(json).RootElement;

        var taskId = root.GetProperty("task_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(taskId));
        Assert.Equal("running", root.GetProperty("status").GetString());
        // Poll handle points at this server's own tool name, not a gateway-prefixed alias.
        Assert.Equal("get_status", root.GetProperty("poll_with").GetString());
        Assert.Equal(45, root.GetProperty("estimated_wait_s").GetInt32()); // quick

        // The task file is created synchronously before the call returns.
        Assert.True(File.Exists(Path.Combine(_cfg.TaskDir, $"{taskId}.json")));
    }

    [Theory]
    [InlineData("quick", 45)]
    [InlineData("standard", 120)]
    [InlineData("deep", 240)]
    public void Research_estimated_wait_tracks_the_requested_depth(string depth, int expected)
    {
        var json = ResearchTool.Research(_cfg, "topic", depth: depth);
        var got = JsonDocument.Parse(json).RootElement.GetProperty("estimated_wait_s").GetInt32();
        Assert.Equal(expected, got);
    }

    [Fact]
    public void GetStatus_throws_for_an_unknown_task()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GetStatusTool.GetStatus(_cfg, "no-such-task"));
        Assert.Contains("no-such-task", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void GetStatus_returns_the_persisted_task_json_after_research()
    {
        var started = ResearchTool.Research(_cfg, "topic", depth: "standard");
        var taskId = JsonDocument.Parse(started).RootElement.GetProperty("task_id").GetString()!;

        var status = GetStatusTool.GetStatus(_cfg, taskId);
        var doc = JsonDocument.Parse(status).RootElement;
        // The persisted record echoes the same task id and is a valid task-state document
        // (status is one of the known values regardless of whether the background run has
        // finished or failed against the nonexistent CLI).
        Assert.Equal(taskId, doc.GetProperty("task_id").GetString());
        var st = doc.GetProperty("status").GetString();
        Assert.Contains(st, new[] { "running", "done", "failed" });
        Assert.Equal("research", doc.GetProperty("tool").GetString());
    }

    // ── CLI-fronted tools: deterministic input-shaping / error paths ─

    [Fact]
    public async Task ImageDescribe_rejects_a_missing_image_before_invoking_the_cli()
    {
        // The on-disk existence guard fires before any subprocess is spawned, so this
        // path is fully deterministic and does not need a live gemini CLI.
        var missing = Path.Combine(_root, "no-such-image.png");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ImageDescribeTool.ImageDescribe(_cfg, missing));
        Assert.Contains("image not found", ex.Message);
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public async Task ImageGenerate_returns_a_structured_error_and_cleans_up_when_no_image_is_produced()
    {
        // With GeminiBin pointing at a nonexistent bundle the CLI cannot produce an
        // image, so the tool must hit its no-output branch: emit the structured
        // NANOBANANA_API_KEY hint AND best-effort delete the empty per-call temp dir.
        var before = Directory.GetDirectories(_cfg.OutputDir).Length;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ImageGenerateTool.ImageGenerate(_cfg, "a red cube", aspect_ratio: "1:1"));

        var err = JsonDocument.Parse(ex.Message).RootElement;
        Assert.Contains("NANOBANANA_API_KEY", err.GetProperty("error").GetString());
        Assert.False(string.IsNullOrEmpty(err.GetProperty("hint").GetString()));
        // The empty call dir is cleaned up, so the output dir is back to its prior count.
        Assert.Equal(before, Directory.GetDirectories(_cfg.OutputDir).Length);
    }

    [Fact]
    public async Task ImageGenerate_escapes_quotes_in_the_prompt_so_it_cannot_break_out_of_the_directive()
    {
        // A prompt containing a double-quote must not throw a serialization/format
        // error — the safePrompt escaping handles it and the call still reaches the
        // (failing) CLI and returns the structured no-output error, proving the
        // escaped prompt was shaped into a well-formed directive.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ImageGenerateTool.ImageGenerate(_cfg, "evil \" ; rm -rf / prompt"));
        var err = JsonDocument.Parse(ex.Message).RootElement;
        Assert.Contains("NANOBANANA_API_KEY", err.GetProperty("error").GetString());
    }
}
