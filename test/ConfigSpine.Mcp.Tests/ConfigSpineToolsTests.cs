using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ConfigSpine.Mcp.Tests;

/// <summary>
/// The tool surface, exercised against a recording fake sink. The two invariants under test:
/// (1) a valid call publishes EXACTLY the rule-6 subject with the reference data shape
///     ({ctid,entity,action,detail}) and reports ok:true — the successful publish shape;
/// (2) any input that would compose a subject outside <c>homelab.config.&gt;</c> is rejected with
///     ok:false and NOTHING is published — the out-of-subtree rejection.
/// The CloudEvents envelope itself (specversion/type/source) is Sinapsi.Nats' tested responsibility;
/// here we pin what the tool hands the sink.
/// </summary>
public sealed class ConfigSpineToolsTests
{
    private sealed class RecordingSink : IConfigEventSink
    {
        public List<(string Subject, JsonObject Data)> Published { get; } = new();

        public Task PublishAsync(string subject, JsonObject data, CancellationToken ct)
        {
            // Clone the data so later mutation can't rewrite what we captured.
            Published.Add((subject, (JsonObject)JsonNode.Parse(data.ToJsonString())!));
            return Task.CompletedTask;
        }
    }

    private static JsonElement Parse(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public async Task Valid_call_publishes_the_rule6_subject_and_reports_ok()
    {
        var sink = new RecordingSink();
        var tools = new ConfigSpineTools(sink);

        var resultJson = await tools.publish_config_event(
            "105", "acl", "added", "granted config-spine-config-emit publish scope",
            CancellationToken.None);
        var result = Parse(resultJson);

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("homelab.config.105.acl.added", result.GetProperty("subject").GetString());

        // Exactly one publish, on exactly the composed subject, with the reference data shape.
        var (subject, data) = Assert.Single(sink.Published);
        Assert.Equal("homelab.config.105.acl.added", subject);
        Assert.Equal("105", (string?)data["ctid"]);
        Assert.Equal("acl", (string?)data["entity"]);
        Assert.Equal("added", (string?)data["action"]);
        Assert.Equal("granted config-spine-config-emit publish scope", (string?)data["detail"]);
    }

    [Fact]
    public async Task Omitted_payload_publishes_empty_detail()
    {
        var sink = new RecordingSink();
        var tools = new ConfigSpineTools(sink);

        var result = Parse(await tools.publish_config_event("138", "cert", "rotated", ct: CancellationToken.None));

        Assert.True(result.GetProperty("ok").GetBoolean());
        var (_, data) = Assert.Single(sink.Published);
        Assert.Equal(string.Empty, (string?)data["detail"]);
    }

    [Theory]
    // ctid that is not a plain number would add tokens / wildcards to the subject.
    [InlineData("10.5", "acl", "added")]
    [InlineData("105a", "acl", "added")]
    [InlineData("*", "acl", "added")]
    // entity / action that would escape their single token.
    [InlineData("105", "*", "added")]
    [InlineData("105", "acl", ">")]
    [InlineData("105", "a.b", "added")]
    [InlineData("105", "acl", "a.b.c")]
    [InlineData("105", "authz", "q1.cse")]     // the exact shape that would land in another subtree
    [InlineData("105", "", "added")]
    [InlineData("105", "acl", "")]
    public async Task Out_of_subtree_input_is_rejected_and_nothing_is_published(
        string ctid, string entity, string action)
    {
        var sink = new RecordingSink();
        var tools = new ConfigSpineTools(sink);

        var result = Parse(await tools.publish_config_event(ctid, entity, action, ct: CancellationToken.None));

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.True(result.TryGetProperty("error", out _));
        Assert.Empty(sink.Published);   // the load-bearing assertion: no publish happened
    }

    [Fact]
    public async Task Publish_failure_is_surfaced_as_a_sanitized_error_not_thrown()
    {
        var tools = new ConfigSpineTools(new ThrowingSink());

        var result = Parse(await tools.publish_config_event("105", "acl", "added", ct: CancellationToken.None));

        Assert.False(result.GetProperty("ok").GetBoolean());
        var err = result.GetProperty("error").GetString()!;
        Assert.DoesNotContain("SUAABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEFGHIJKLMNOPQRST", err);
    }

    private sealed class ThrowingSink : IConfigEventSink
    {
        public Task PublishAsync(string subject, JsonObject data, CancellationToken ct) =>
            throw new InvalidOperationException(
                "NATS publish failed for seed SUAABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEFGHIJKLMNOPQRST");
    }
}
