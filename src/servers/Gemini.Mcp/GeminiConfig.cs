namespace Gemini.Mcp;

/// <summary>
/// Runtime configuration resolved from environment variables. Every path and
/// timeout is env-driven so the server can be pointed at any layout without a
/// rebuild; the defaults below are generic local-lab placeholders, not a fixed
/// deployment.
///
/// <c>ResearchTimeoutMs</c> is intentionally separate from the per-call
/// <c>DefaultTimeoutMs</c>: a deep web-research run legitimately takes far
/// longer than an interactive prompt, so it gets its own (longer, still
/// env-overridable) budget via <c>GEMINI_RESEARCH_TIMEOUT_MS</c> — default
/// 30 minutes — rather than being capped by the interactive timeout.
/// </summary>
public sealed record GeminiConfig(
    string OutputDir,
    string SessionDir,
    string TaskDir,
    string GeminiBin,
    int DefaultTimeoutMs,
    int ResearchTimeoutMs)
{
    public static GeminiConfig FromEnvironment() => new(
        OutputDir: Environment.GetEnvironmentVariable("NANO_BANANA_OUTPUT_DIR") ?? "/var/lib/nano-banana/output",
        SessionDir: Environment.GetEnvironmentVariable("GEMINI_SESSION_DIR") ?? "/var/lib/gemini-mcp/sessions",
        TaskDir: Environment.GetEnvironmentVariable("GEMINI_TASK_DIR") ?? "/var/lib/gemini-mcp/tasks",
        // Path to the gemini-cli bundle entry. We invoke `node <this>` directly
        // (see GeminiCli.cs) rather than the wrapper symlink, so this points at
        // the bundle's JS entrypoint.
        GeminiBin: Environment.GetEnvironmentVariable("GEMINI_BIN") ?? "/usr/local/lib/node_modules/@google/gemini-cli/bundle/gemini.js",
        DefaultTimeoutMs: ParseInt(Environment.GetEnvironmentVariable("GEMINI_TIMEOUT_MS"), 180_000),
        // Deep-research budget: 30 min default, env-configurable for very deep runs.
        ResearchTimeoutMs: ParseInt(Environment.GetEnvironmentVariable("GEMINI_RESEARCH_TIMEOUT_MS"), 1_800_000));

    private static int ParseInt(string? raw, int fallback) =>
        int.TryParse(raw, out var v) ? v : fallback;
}
