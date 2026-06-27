using Xunit;

namespace SageCouncil.Mcp.Tests;

/// <summary>
/// CouncilOptions.FromEnvironment() is env-driven; these tests set/clear the
/// relevant variables on the current process and assert the parse. They run
/// serially (the "env" collection) so concurrent env mutation can't cross-talk.
/// The OIDC issuer/audience/key-dir knobs are not on CouncilOptions any more —
/// they belong to the Sinapsi.AgentJwt library — so they are intentionally not
/// asserted here.
/// </summary>
[Collection("env")]
public class CouncilOptionsTests
{
    private static IDisposable Env(params (string Key, string? Value)[] vars)
    {
        var prior = vars.Select(v => (v.Key, Old: Environment.GetEnvironmentVariable(v.Key))).ToArray();
        foreach (var (k, v) in vars) Environment.SetEnvironmentVariable(k, v);
        return new Restore(prior);
    }

    private sealed class Restore((string Key, string? Old)[] prior) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (k, old) in prior) Environment.SetEnvironmentVariable(k, old);
        }
    }

    [Fact]
    public void Defaults_are_neutral_and_local()
    {
        using var _ = Env(
            ("AGENT_BACKEND_URL", null), ("GATEWAY_URL", null), ("AGENT_MODEL", null),
            ("COUNCIL_CLAUDE_AGENT", null), ("PERSONA_DIR", "/nonexistent-persona-dir"));

        var opt = CouncilOptions.FromEnvironment();

        Assert.Equal("http://127.0.0.1:8088/", opt.BackendUrl.ToString());
        Assert.Equal("http://127.0.0.1:8443/mcp", opt.GatewayUrl.ToString());
        Assert.Equal("claude-sonnet-4-6", opt.Model);
        Assert.Equal("agent-council-claude", opt.ClaudeAgent);
        Assert.Equal("agent-council-gemini", opt.GeminiAgent);
        Assert.Equal("agent-council-chatgpt", opt.ChatgptAgent);
        Assert.Empty(opt.ExtraPersonas);
    }

    [Fact]
    public void Env_overrides_urls_model_timeouts_and_roster()
    {
        using var _ = Env(
            ("AGENT_BACKEND_URL", "http://backend.test:9000"),
            ("GATEWAY_URL", "http://gw.test:7000/mcp"),
            ("AGENT_MODEL", "some-model"),
            ("SAGE_TIMEOUT_MS", "60000"),
            ("SAGE_MEMBER_TIMEOUT_MS", "30000"),
            ("COUNCIL_CLAUDE_AGENT", "renamed-claude"),
            ("PERSONA_DIR", "/nonexistent-persona-dir"));

        var opt = CouncilOptions.FromEnvironment();

        Assert.Equal("http://backend.test:9000/", opt.BackendUrl.ToString());
        Assert.Equal("http://gw.test:7000/mcp", opt.GatewayUrl.ToString());
        Assert.Equal("some-model", opt.Model);
        Assert.Equal(TimeSpan.FromMilliseconds(60000), opt.Timeout);
        Assert.Equal(TimeSpan.FromMilliseconds(30000), opt.MemberDeadline);
        Assert.Equal("renamed-claude", opt.ClaudeAgent);
    }

    [Fact]
    public void Persona_overlay_loads_markdown_files_from_persona_dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "council-personas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "code-review.md"), "  custom code-review persona  ");
            File.WriteAllText(Path.Combine(dir, "empty.md"), "   "); // blank → ignored

            using var _ = Env(("PERSONA_DIR", dir));
            var opt = CouncilOptions.FromEnvironment();

            Assert.True(opt.ExtraPersonas.ContainsKey("code-review"));
            Assert.Equal("custom code-review persona", opt.ExtraPersonas["code-review"]);
            Assert.False(opt.ExtraPersonas.ContainsKey("empty"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
