using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gemini.Mcp;

/// <summary>Persisted async-task state file format.</summary>
public sealed class TaskState
{
    [JsonPropertyName("task_id")] public string TaskId { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "running"; // running | done | failed
    [JsonPropertyName("started_at")] public long StartedAt { get; set; }
    [JsonPropertyName("ended_at")] public long? EndedAt { get; set; }
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    [JsonPropertyName("prompt_head")] public string PromptHead { get; set; } = "";
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>Persisted session state file format.</summary>
public sealed class SessionState
{
    [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
    [JsonPropertyName("focus")] public string? Focus { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("prompt_count")] public int PromptCount { get; set; }
    [JsonPropertyName("last_activity_at")] public long? LastActivityAt { get; set; }
    [JsonPropertyName("gemini_session_id")] public string? GeminiSessionId { get; set; }
}

/// <summary>Shared JSON options. Indented, web-cased, null-skipping.</summary>
internal static class Jsons
{
    public static readonly JsonSerializerOptions IndentedWeb =
        new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Now in Unix milliseconds.</summary>
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Trim from the right end up to <paramref name="max"/> characters (for a stderr tail).</summary>
    public static string TailLeft(string s, int max) => s.Length <= max ? s : s.Substring(s.Length - max, max);
}
