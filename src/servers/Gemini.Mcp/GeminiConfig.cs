// ---------------------------------------------------------------------------
// GeminiConfig — env-driven runtime configuration, bound + validated once at
// startup. Mirrors the StepCa.Mcp exemplar (StepCaOptions): neutral defaults so
// the binary carries no site-specific wiring, and FAIL-CLOSED timeout parsing —
// a non-numeric / <= 0 / out-of-range timeout THROWS naming the offending env
// var (failing startup) rather than being silently swallowed into a default that
// would either make every call time out instantly or, negative, throw deep in the
// CancellationTokenSource ctor.
// ---------------------------------------------------------------------------
namespace Gemini.Mcp;

/// <summary>
/// Runtime configuration resolved from environment variables. Every path and
/// timeout is env-driven so the server can be pointed at any layout without a
/// rebuild; the defaults below are generic local-lab placeholders, not a fixed
/// deployment.
///
/// <para>
/// <c>ResearchTimeoutMs</c> is intentionally separate from the per-call
/// <c>DefaultTimeoutMs</c>: a deep web-research run legitimately takes far longer
/// than an interactive prompt, so it gets its own (longer, still env-overridable)
/// budget via <c>GEMINI_RESEARCH_TIMEOUT_MS</c> — default 30 minutes — rather than
/// being capped by the interactive timeout. Both timeouts are validated with the
/// same fail-closed rules and clamped to a hard ceiling.
/// </para>
/// </summary>
public sealed record GeminiConfig(
    string OutputDir,
    string SessionDir,
    string TaskDir,
    string GeminiBin,
    int DefaultTimeoutMs,
    int ResearchTimeoutMs)
{
    /// <summary>Default per-call interactive subprocess timeout (ms).</summary>
    internal const int DefaultInteractiveTimeoutMs = 180_000;

    /// <summary>Default deep-research subprocess timeout (ms) — 30 minutes.</summary>
    internal const int DefaultResearchTimeoutMs = 1_800_000;

    /// <summary>Upper bound on a configurable subprocess timeout (ms). One hour is
    /// far past any legitimate gemini run (even a deep-research one); a larger
    /// value is treated as a config error, not honoured.</summary>
    internal const int MaxTimeoutMs = 3_600_000;

    public static GeminiConfig FromEnvironment() => new(
        OutputDir: Environment.GetEnvironmentVariable("NANO_BANANA_OUTPUT_DIR") ?? "/var/lib/nano-banana/output",
        SessionDir: Environment.GetEnvironmentVariable("GEMINI_SESSION_DIR") ?? "/var/lib/gemini-mcp/sessions",
        TaskDir: Environment.GetEnvironmentVariable("GEMINI_TASK_DIR") ?? "/var/lib/gemini-mcp/tasks",
        // Path to the gemini-cli bundle entry. We invoke `node <this>` directly
        // (see GeminiCli.cs) rather than the wrapper symlink, so this points at
        // the bundle's JS entrypoint.
        GeminiBin: Environment.GetEnvironmentVariable("GEMINI_BIN") ?? "/usr/local/lib/node_modules/@google/gemini-cli/bundle/gemini.js",
        DefaultTimeoutMs: ReadTimeoutMs("GEMINI_TIMEOUT_MS", DefaultInteractiveTimeoutMs),
        // Deep-research budget: 30 min default, env-configurable for very deep runs.
        ResearchTimeoutMs: ReadTimeoutMs("GEMINI_RESEARCH_TIMEOUT_MS", DefaultResearchTimeoutMs));

    /// <summary>
    /// Read + validate a subprocess-timeout env var. When unset, the neutral
    /// <paramref name="fallback"/> is used. When SET, the value must be an integer
    /// in <c>1..<see cref="MaxTimeoutMs"/></c> ms; a non-numeric, <c>&lt;= 0</c>, or
    /// out-of-range value is a config error — we throw naming the offending var
    /// (failing startup) rather than silently swallowing a footgun into the default.
    /// </summary>
    private static int ReadTimeoutMs(string envVar, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(raw))
            return fallback;

        if (!int.TryParse(raw, out var ms) || ms <= 0 || ms > MaxTimeoutMs)
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected an integer in 1..{MaxTimeoutMs} ms (default {fallback}).");

        return ms;
    }
}
