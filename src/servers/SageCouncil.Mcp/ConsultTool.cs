using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace SageCouncil.Mcp;

[McpServerToolType]
public sealed class ConsultTool
{
    private static readonly JsonSerializerOptions _json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [McpServerTool(Name = "consult")]
    [Description(
        "Convene a multi-AI research council on a HARD, articulated problem — a genuine design, " +
        "architecture, trade-off, or second-opinion question that you cannot confidently resolve " +
        "yourself. NOT for trivial lookups or things you can answer locally. Each member " +
        "(claude / gemini / chatgpt, each behind its own restricted identity) runs its vendor CLI in " +
        "research mode — Claude Code, Gemini deep-research, Codex agent — and does real multi-step " +
        "research, so a consult takes MINUTES, not seconds — accuracy and depth are the point, speed " +
        "is not. This call is ASYNCHRONOUS: it dispatches the council and returns a job_id immediately " +
        "so you are free to do other work. Collect the synthesised answer later with `consult_result`.")]
    public static string Consult(
        ConsultJobStore store,
        [Description("The hard question to consult on. Be specific and give the relevant context — the members do not share your session.")]
        string prompt,
        [Description("Persona/focus the members adopt: general | code-review | architecture | second-opinion | deep-research | design (UX/IA, for journey-driven design). Expandable: drop a `<name>.md` file in the server's PERSONA_DIR to add more.")]
        string focus = "general",
        [Description("Council members: claude-research | gemini-research | chatgpt-research. Default: all three for a real cross-vendor council.")]
        string[]? members = null)
    {
        var roster = members is { Length: > 0 }
            ? members
            : ["claude-research", "gemini-research", "chatgpt-research"];
        var job = store.Start(prompt, focus, roster);

        return JsonSerializer.Serialize(new
        {
            job_id = job.Id,
            status = "running",
            focus,
            members = roster,
            started_at = job.StartedAt,
            note = "Council dispatched. Go do other work, then call consult_result with this job_id "
                 + "to collect the synthesised answer. Deep research can take several minutes — poll, "
                 + "don't busy-wait.",
        }, _json);
    }

    [McpServerTool(Name = "consult_result")]
    [Description(
        "Poll a council consult launched by `consult`. Returns status=running while the council is " +
        "still researching, status=done with the full members + synthesis result when finished, or " +
        "status=error on failure. Safe to call repeatedly; results are retained for ~1 hour after completion.")]
    public static string ConsultResult(
        ConsultJobStore store,
        [Description("The job_id returned by a prior `consult` call.")] string job_id)
    {
        var job = store.Get(job_id);
        if (job is null)
            return JsonSerializer.Serialize(new
            {
                job_id,
                status = "not_found",
                error = "no such job_id (it may have expired after the ~1h retention window, or never existed)",
            }, _json);

        return job.Snapshot(_json);
    }
}
