using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Gemini.Mcp;

[McpServerToolType]
public sealed class SessionCreateTool
{
    [McpServerTool(Name = "session_create")]
    [Description("Create a new gemini session. Returns the session_id to use with session_resume/session_close.")]
    public static string SessionCreate(
        GeminiConfig cfg,
        [Description("Optional focus marker / context hint.")] string? focus = null)
    {
        var sid = Guid.NewGuid().ToString();
        var dir = Path.Combine(cfg.SessionDir, sid);
        Directory.CreateDirectory(dir);

        if (!string.IsNullOrEmpty(focus))
            File.WriteAllText(Path.Combine(dir, "focus.txt"), focus);

        var state = new SessionState
        {
            SessionId = sid,
            Focus = focus,
            CreatedAt = Jsons.NowMs(),
            PromptCount = 0,
        };
        File.WriteAllText(Path.Combine(dir, "state.json"),
            JsonSerializer.Serialize(state, Jsons.IndentedWeb));

        return JsonSerializer.Serialize(new { session_id = sid }, Jsons.IndentedWeb);
    }
}

[McpServerToolType]
public sealed class SessionResumeTool
{
    [McpServerTool(Name = "session_resume")]
    [Description("Resume a gemini session by id, sending a new prompt. Increments the session's prompt_count.")]
    public static async Task<string> SessionResume(
        GeminiConfig cfg,
        [Description("The session_id from session_create.")] string session_id,
        [Description("The new prompt to send in this session.")] string prompt)
    {
        var dir = Path.Combine(cfg.SessionDir, session_id);
        var statePath = Path.Combine(dir, "state.json");
        if (!File.Exists(statePath))
            throw new InvalidOperationException($"session {session_id} not found");

        var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(statePath), Jsons.IndentedWeb)
            ?? throw new InvalidOperationException($"session {session_id} state unreadable");

        var args = new List<string> { "-p", prompt };
        if (!string.IsNullOrEmpty(state.GeminiSessionId))
        {
            args.Add("--resume");
            args.Add(state.GeminiSessionId);
        }
        args.AddRange(["--skip-trust", "--yolo"]);

        var r = await GeminiCli.RunAsync(cfg, args.ToArray(), cwd: dir);

        if (r.ExitCode != 0)
            throw new InvalidOperationException($"gemini exited {r.ExitCode}: {Jsons.TailLeft(r.Stderr, 300)}");

        state.PromptCount++;
        state.LastActivityAt = Jsons.NowMs();
        File.WriteAllText(statePath, JsonSerializer.Serialize(state, Jsons.IndentedWeb));

        return r.Stdout.TrimEnd();
    }
}

[McpServerToolType]
public sealed class SessionCloseTool
{
    [McpServerTool(Name = "session_close")]
    [Description("Close a gemini session, removing its on-disk state. No error if the session is already absent.")]
    public static string SessionClose(
        GeminiConfig cfg,
        [Description("The session_id to close.")] string session_id)
    {
        var dir = Path.Combine(cfg.SessionDir, session_id);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        return JsonSerializer.Serialize(new { session_id, closed = true }, Jsons.IndentedWeb);
    }
}
