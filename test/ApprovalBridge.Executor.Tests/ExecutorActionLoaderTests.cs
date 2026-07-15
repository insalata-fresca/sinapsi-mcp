using ApprovalBridge.Executor.Garmin;
using ApprovalBridge.Executor.Registry;
using Xunit;

namespace ApprovalBridge.Executor.Tests;

/// <summary>
/// The executor-side loader parses the REAL E1.1 allowlist YAML (fixture copy) into a definition, capturing
/// the fields the executor needs — <c>executor</c>, <c>target.identity</c>, and (unlike the broker)
/// <c>result_schema</c> + its declared property set. Strict/deny-by-default on malformed entries.
/// </summary>
public sealed class ExecutorActionLoaderTests
{
    private static string GarminYaml => Path.Combine(Fixtures.FixturesDir, "garmin.oauth.exchange.yaml");

    [Fact]
    public void ParsesTheRealGarminEntry()
    {
        var def = ExecutorActionLoader.ParseFile(GarminYaml);
        Assert.Equal("garmin.oauth.exchange", def.ActionId);
        Assert.Equal(GarminOAuthExchangeExecutor.Name, def.ExecutorName);
        Assert.Equal("garmin-connector", def.TargetIdentity);
        Assert.Equal(new HashSet<string> { "status", "stored", "expires_at" }, def.ResultProperties);
    }

    [Fact]
    public void LoadDirectory_FindsTheAllowlistedActionById()
    {
        var source = ExecutorActionLoader.LoadDirectory(Fixtures.FixturesDir);
        Assert.NotNull(source.Find("garmin.oauth.exchange"));
        Assert.Null(source.Find("not.registered"));
    }

    [Fact]
    public void ParsedResultSchema_AcceptsTheNonSecretShape_AndRejectsABadStatus()
    {
        var def = ExecutorActionLoader.ParseFile(GarminYaml);
        var ok = def.ResultSchema.Evaluate(System.Text.Json.Nodes.JsonNode.Parse(
            """{ "status": "ok", "stored": true, "expires_at": "2026-09-01T12:00:00Z" }"""));
        Assert.True(ok.IsValid);
        var bad = def.ResultSchema.Evaluate(System.Text.Json.Nodes.JsonNode.Parse("""{ "status": "leaked" }"""));
        Assert.False(bad.IsValid);
    }

    [Fact]
    public void MissingResultSchema_IsRefused()
    {
        // filename stem must equal action_id, so name the action file accordingly.
        var actionPath = Path.Combine(Path.GetTempPath(), "demo.noresult.yaml");
        File.WriteAllText(actionPath, """
            action_id: demo.noresult
            executor: x
            target: { identity: y }
            param_schema: { type: object, additionalProperties: false, properties: {} }
            """);
        try
        {
            Assert.Throws<InvalidDataException>(() => ExecutorActionLoader.ParseFile(actionPath));
        }
        finally { File.Delete(actionPath); }
    }
}
