using System.Text.Json;
using Gemini.Mcp;
using Xunit;

namespace Gemini.Mcp.Tests;

/// <summary>
/// The async-task and session state files are the server's persistence format: they are
/// written by one call and read back by another (get_status, session_resume). These tests
/// pin the on-disk JSON shape — snake_case property names and null-omission — so a format
/// drift that would silently break cross-call reads is caught.
/// </summary>
public sealed class StateSerializationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TaskState_uses_snake_case_property_names()
    {
        var s = new TaskState
        {
            TaskId = "abc",
            Status = "running",
            StartedAt = 1700000000000,
            Tool = "research",
            PromptHead = "hello",
        };

        var json = JsonSerializer.Serialize(s, Web);

        Assert.Contains("\"task_id\":\"abc\"", json);
        Assert.Contains("\"started_at\":1700000000000", json);
        Assert.Contains("\"prompt_head\":\"hello\"", json);
        // PascalCase must NOT leak into the persisted file.
        Assert.DoesNotContain("\"TaskId\"", json);
    }

    [Fact]
    public void TaskState_round_trips_through_serialization()
    {
        var s = new TaskState
        {
            TaskId = "t1", Status = "done", StartedAt = 1, EndedAt = 2,
            Tool = "research", PromptHead = "q", Result = "answer", Error = null,
        };

        var back = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(s, Web), Web)!;

        Assert.Equal("t1", back.TaskId);
        Assert.Equal("done", back.Status);
        Assert.Equal(2, back.EndedAt);
        Assert.Equal("answer", back.Result);
        Assert.Null(back.Error);
    }

    [Fact]
    public void SessionState_round_trips_and_keeps_snake_case()
    {
        var s = new SessionState
        {
            SessionId = "s1",
            Focus = "debug a flaky test",
            CreatedAt = 42,
            PromptCount = 3,
            LastActivityAt = 99,
            GeminiSessionId = "g-77",
        };

        var json = JsonSerializer.Serialize(s, Web);
        Assert.Contains("\"session_id\":\"s1\"", json);
        Assert.Contains("\"prompt_count\":3", json);
        Assert.Contains("\"gemini_session_id\":\"g-77\"", json);

        var back = JsonSerializer.Deserialize<SessionState>(json, Web)!;
        Assert.Equal("s1", back.SessionId);
        Assert.Equal(3, back.PromptCount);
        Assert.Equal("g-77", back.GeminiSessionId);
    }
}
