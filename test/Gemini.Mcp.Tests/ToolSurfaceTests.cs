using System.Text.Json;
using Gemini.Mcp;
using Xunit;

namespace Gemini.Mcp.Tests;

/// <summary>
/// Exercises the deterministic, filesystem-backed part of the tool surface — the behaviour
/// that does not depend on the external <c>gemini</c> CLI subprocess: session lifecycle,
/// the async-task handle that <c>research</c> hands back, and the not-found error paths of
/// <c>get_status</c> / <c>session_resume</c>. Each test runs against its own temp dirs.
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
}
