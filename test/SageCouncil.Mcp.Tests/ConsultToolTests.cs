using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sinapsi.AgentJwt;
using Sinapsi.Mcp;
using Xunit;

namespace SageCouncil.Mcp.Tests;

/// <summary>
/// Exercises the public tool surface (`consult` / `consult_result`) and the
/// background job machinery behind it. The roster here is a member type the
/// dispatcher does not recognise: <see cref="CouncilService"/> returns an
/// immediate error result for an unknown member with no network I/O, so the
/// whole Start → RunAsync → ConsultAsync → synthesis-skip → Complete path runs
/// deterministically and fast.
/// </summary>
public class ConsultToolTests
{
    private static ConsultJobStore NewStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new CouncilOptions
        {
            // Keep the per-job ceiling tiny so a hung path can never stall a test.
            Timeout = TimeSpan.FromSeconds(2),
            MemberDeadline = TimeSpan.FromSeconds(2),
        });
        services.AddSingleton(new AgentJwtOptions());
        services.AddHttpClient<GatewayMcpClient>();
        services.AddHttpClient<CouncilService>();
        services.AddHttpClient<AgentJwtMinter>();
        services.AddSingleton<ConsultJobStore>();
        return services.BuildServiceProvider().GetRequiredService<ConsultJobStore>();
    }

    private static async Task<JsonElement> PollToTerminalAsync(ConsultJobStore store, string jobId)
    {
        for (var i = 0; i < 100; i++)
        {
            var snap = JsonDocument.Parse(ConsultTool.ConsultResult(store, jobId)).RootElement;
            var status = snap.GetProperty("status").GetString();
            if (status is "done" or "error")
                return snap.Clone();
            await Task.Delay(50);
        }
        throw new TimeoutException("job did not reach a terminal state");
    }

    [Fact]
    public void Consult_defaults_to_the_full_three_member_roster()
    {
        var store = NewStore();
        var doc = JsonDocument.Parse(ConsultTool.Consult(store, "a hard question")).RootElement;

        Assert.Equal("running", doc.GetProperty("status").GetString());
        Assert.Equal("general", doc.GetProperty("focus").GetString());
        var members = doc.GetProperty("members").EnumerateArray().Select(m => m.GetString()).ToArray();
        Assert.Equal(new[] { "claude-research", "gemini-research", "chatgpt-research" }, members);
        Assert.StartsWith("council-", doc.GetProperty("job_id").GetString());
    }

    [Fact]
    public void Consult_honours_an_explicit_roster_and_focus()
    {
        var store = NewStore();
        var doc = JsonDocument.Parse(
            ConsultTool.Consult(store, "q", focus: "architecture", members: ["gemini-research"])).RootElement;

        Assert.Equal("architecture", doc.GetProperty("focus").GetString());
        Assert.Equal(new[] { "gemini-research" },
            doc.GetProperty("members").EnumerateArray().Select(m => m.GetString()).ToArray());
    }

    [Fact]
    public void ConsultResult_for_an_unknown_job_id_reports_not_found()
    {
        var store = NewStore();
        var doc = JsonDocument.Parse(ConsultTool.ConsultResult(store, "council-doesnotexist")).RootElement;

        Assert.Equal("not_found", doc.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(doc.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task A_consult_with_no_usable_members_completes_done_with_a_skipped_synthesis()
    {
        var store = NewStore();
        // "noop" is not a known member type → CouncilService dispatches it to an
        // immediate error result; with zero usable answers the synthesis is skipped.
        var start = JsonDocument.Parse(ConsultTool.Consult(store, "q", members: ["noop"])).RootElement;
        var jobId = start.GetProperty("job_id").GetString()!;

        var snap = await PollToTerminalAsync(store, jobId);

        Assert.Equal("done", snap.GetProperty("status").GetString());
        Assert.True(snap.GetProperty("elapsed_ms").GetInt64() >= 0);

        var result = snap.GetProperty("result");
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        // The lone member surfaces its dispatch error...
        var member = result.GetProperty("members").EnumerateArray().Single();
        Assert.Equal("noop", member.GetProperty("member").GetString());
        Assert.Contains("unknown member type", member.GetProperty("error").GetString());
        // ...and the synthesis is the explicit skip sentinel.
        Assert.Contains("synthesis skipped", result.GetProperty("synthesis").GetString());
    }
}
