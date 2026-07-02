using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sinapsi.AgentJwt;
using Sinapsi.Mcp;
using Xunit;

namespace SageCouncil.Mcp.Tests;

// -----------------------------------------------------------------------------
// Tool-guard coverage: invalid input to `consult` / `consult_result` must return
// the {ok:false,error} envelope and SHORT-CIRCUIT before any side effect — no
// job is dispatched, no store lookup mutates anything. Mirrors the reference-grade
// MutatingToolGuardTests, which point the backend at an unreachable path so the
// test proves the guard fires *before* the side effect, not after.
// -----------------------------------------------------------------------------

public sealed class ConsultToolGuardTests
{
    private static ConsultJobStore NewStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Point the backend + gateway at an unroutable host so that IF a guard
        // ever failed to short-circuit and a job actually dispatched, the network
        // call would fail loudly rather than silently pass. The guard tests below
        // assert the envelope is returned synchronously with NO job_id, proving no
        // dispatch happened at all.
        services.AddSingleton(new CouncilOptions
        {
            BackendUrl = new Uri("http://127.0.0.1:1"),
            GatewayUrl = new Uri("http://127.0.0.1:1/mcp"),
            Timeout = TimeSpan.FromSeconds(1),
            MemberDeadline = TimeSpan.FromSeconds(1),
        });
        services.AddSingleton(new AgentJwtOptions());
        services.AddHttpClient<GatewayMcpClient>();
        services.AddHttpClient<CouncilService>();
        services.AddHttpClient<AgentJwtMinter>();
        services.AddSingleton<ConsultJobStore>();
        return services.BuildServiceProvider().GetRequiredService<ConsultJobStore>();
    }

    private static JsonElement Call(Func<string> tool) => JsonDocument.Parse(tool()).RootElement;

    // ── consult ──────────────────────────────────────────────────────────────

    [Fact]
    public void Consult_with_a_blank_prompt_returns_a_structured_error_and_dispatches_no_job()
    {
        var store = NewStore();
        var doc = Call(() => ConsultTool.Consult(store, "   "));

        Assert.False(doc.GetProperty("ok").GetBoolean());
        Assert.Contains("prompt is required", doc.GetProperty("error").GetString());
        // Proof of short-circuit: the success envelope carries a job_id; the error
        // envelope must NOT — no job was ever started.
        Assert.False(doc.TryGetProperty("job_id", out _));
    }

    [Theory]
    [InlineData("hard\0question")]                       // NUL control char
    public void Consult_with_a_control_char_prompt_is_rejected(string prompt)
    {
        var store = NewStore();
        var doc = Call(() => ConsultTool.Consult(store, prompt));

        Assert.False(doc.GetProperty("ok").GetBoolean());
        Assert.Contains("control characters", doc.GetProperty("error").GetString());
        Assert.False(doc.TryGetProperty("job_id", out _));
    }

    [Theory]
    [InlineData("-rf")]                                  // leading-dash focus
    [InlineData("bad\nfocus")]                           // newline in focus
    public void Consult_with_an_invalid_focus_is_rejected(string focus)
    {
        var store = NewStore();
        var doc = Call(() => ConsultTool.Consult(store, "a real question", focus: focus));

        Assert.False(doc.GetProperty("ok").GetBoolean());
        Assert.False(doc.TryGetProperty("job_id", out _));
    }

    [Fact]
    public void Consult_with_a_leading_dash_member_is_rejected()
    {
        var store = NewStore();
        var doc = Call(() => ConsultTool.Consult(store, "q", members: new[] { "-rf" }));

        Assert.False(doc.GetProperty("ok").GetBoolean());
        Assert.Contains("must not start with '-'", doc.GetProperty("error").GetString());
        Assert.False(doc.TryGetProperty("job_id", out _));
    }

    [Fact]
    public void Consult_with_a_valid_request_still_dispatches_a_job()
    {
        // Parity guard: the added validation must not block a legitimate consult.
        var store = NewStore();
        var doc = Call(() => ConsultTool.Consult(store, "a genuinely hard question", members: new[] { "noop" }));

        Assert.Equal("running", doc.GetProperty("status").GetString());
        Assert.StartsWith("council-", doc.GetProperty("job_id").GetString());
    }

    // ── consult_result ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ConsultResult_with_a_blank_job_id_returns_a_structured_error(string jobId)
    {
        var store = NewStore();
        var doc = Call(() => ConsultTool.ConsultResult(store, jobId));

        Assert.False(doc.GetProperty("ok").GetBoolean());
        Assert.Contains("job_id is required", doc.GetProperty("error").GetString());
    }

    [Fact]
    public void ConsultResult_with_a_control_char_job_id_is_rejected_before_the_lookup()
    {
        var store = NewStore();
        var doc = Call(() => ConsultTool.ConsultResult(store, "council-\0abc"));

        Assert.False(doc.GetProperty("ok").GetBoolean());
        Assert.Contains("control characters", doc.GetProperty("error").GetString());
        // A malformed id short-circuits before the store lookup, so it is NOT the
        // regular not_found envelope (which carries a status field).
        Assert.False(doc.TryGetProperty("status", out _));
    }

    [Fact]
    public void ConsultResult_with_a_well_formed_unknown_id_still_reports_not_found()
    {
        // Parity guard: a clean-but-unknown id passes validation and reaches the
        // real not_found path.
        var store = NewStore();
        var doc = Call(() => ConsultTool.ConsultResult(store, "council-deadbeef0000"));

        Assert.Equal("not_found", doc.GetProperty("status").GetString());
    }
}
