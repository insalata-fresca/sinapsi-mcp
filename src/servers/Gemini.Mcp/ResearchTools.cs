using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Gemini.Mcp;

[McpServerToolType]
public sealed class ResearchTool
{
    /// <summary>
    /// Async research: mint a task_id, return immediately with status=running, run the
    /// gemini CLI in a fire-and-forget background <see cref="Task"/>, and write the
    /// result back to the task file when it completes.
    ///
    /// Honours <see cref="GeminiConfig.ResearchTimeoutMs"/> (default 30 min) so a deep
    /// research run is not capped by the shorter interactive timeout.
    /// </summary>
    [McpServerTool(Name = "research")]
    [Description("Run a deep web-research query via Gemini Pro. Asynchronous: returns task_id immediately; poll with get_status.")]
    public static string Research(
        GeminiConfig cfg,
        [Description("The research query / topic.")] string query,
        [Description("Depth: quick | standard | deep")] string depth = "standard")
    {
        var taskId = Guid.NewGuid().ToString();
        var taskFile = Path.Combine(cfg.TaskDir, $"{taskId}.json");

        var state = new TaskState
        {
            TaskId = taskId,
            Status = "running",
            StartedAt = Jsons.NowMs(),
            Tool = "research",
            PromptHead = query.Length <= 100 ? query : query.Substring(0, 100),
        };
        File.WriteAllText(taskFile, JsonSerializer.Serialize(state, Jsons.IndentedWeb));

        var directive =
            $"Do a {depth}-depth research on the following topic. " +
            "Use web search, synthesize findings from at least 3 sources, " +
            $"include inline citations. Topic: {query}";

        // Fire-and-forget background task. The MCP call returns immediately; the
        // gemini CLI runs in the background with the (env-configurable) research
        // timeout. On completion, update the task file with result or error.
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await GeminiCli.RunAsync(cfg,
                    ["-p", directive, "--model", "pro", "--skip-trust", "--yolo"],
                    cwd: cfg.SessionDir,
                    timeoutMsOverride: cfg.ResearchTimeoutMs);

                state.EndedAt = Jsons.NowMs();
                if (r.ExitCode == 0)
                {
                    state.Status = "done";
                    state.Result = r.Stdout;
                }
                else
                {
                    state.Status = "failed";
                    state.Error = Jsons.TailLeft(r.Stderr, 500);
                }
            }
            catch (Exception e)
            {
                state.EndedAt = Jsons.NowMs();
                state.Status = "failed";
                state.Error = e.Message;
            }
            try { File.WriteAllText(taskFile, JsonSerializer.Serialize(state, Jsons.IndentedWeb)); }
            catch { /* best-effort */ }
        });

        var estimatedWait = depth switch { "quick" => 45, "deep" => 240, _ => 120 };
        return JsonSerializer.Serialize(new
        {
            task_id = taskId,
            status = "running",
            poll_with = "get_status",
            estimated_wait_s = estimatedWait,
        }, Jsons.IndentedWeb);
    }
}

[McpServerToolType]
public sealed class GetStatusTool
{
    [McpServerTool(Name = "get_status")]
    [Description("Poll the status of an async task (e.g. one returned by research). Returns the persisted task state.")]
    public static string GetStatus(
        GeminiConfig cfg,
        [Description("The task_id returned by an async tool.")] string task_id)
    {
        var taskFile = Path.Combine(cfg.TaskDir, $"{task_id}.json");
        if (!File.Exists(taskFile))
            throw new InvalidOperationException($"task {task_id} not found");
        return File.ReadAllText(taskFile);
    }
}
