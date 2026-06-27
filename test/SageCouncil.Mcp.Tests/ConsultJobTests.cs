using System.Text.Json;
using Xunit;

namespace SageCouncil.Mcp.Tests;

/// <summary>Direct unit tests for the <see cref="ConsultJob"/> state machine + snapshot.</summary>
public class ConsultJobTests
{
    private static ConsultJob NewJob() => new()
    {
        Id = "council-test",
        Prompt = "p",
        Focus = "general",
        Members = new[] { "claude-research" },
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_fresh_job_snapshots_as_running_with_no_result_or_error()
    {
        var snap = JsonDocument.Parse(NewJob().Snapshot(Json)).RootElement;

        Assert.Equal("running", snap.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, snap.GetProperty("result").ValueKind);
        Assert.Equal(JsonValueKind.Null, snap.GetProperty("error").ValueKind);
        Assert.Equal(JsonValueKind.Null, snap.GetProperty("completed_at").ValueKind);
    }

    [Fact]
    public void Complete_embeds_the_council_json_as_a_nested_object()
    {
        var job = NewJob();
        job.Complete("""{"prompt":"p","synthesis":"the answer"}""");

        var snap = JsonDocument.Parse(job.Snapshot(Json)).RootElement;
        Assert.Equal("done", snap.GetProperty("status").GetString());
        var result = snap.GetProperty("result");
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal("the answer", result.GetProperty("synthesis").GetString());
        Assert.NotEqual(JsonValueKind.Null, snap.GetProperty("completed_at").ValueKind);
    }

    [Fact]
    public void Fail_records_the_error_and_leaves_result_null()
    {
        var job = NewJob();
        job.Fail("backend exploded");

        var snap = JsonDocument.Parse(job.Snapshot(Json)).RootElement;
        Assert.Equal("error", snap.GetProperty("status").GetString());
        Assert.Equal("backend exploded", snap.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, snap.GetProperty("result").ValueKind);
    }
}
