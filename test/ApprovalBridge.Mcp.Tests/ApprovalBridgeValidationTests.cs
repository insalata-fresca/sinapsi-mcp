using Xunit;

namespace ApprovalBridge.Mcp.Tests;

/// <summary>Call-SHAPE validation (docs/66 §3.1) — deliberately independent of the broker's own
/// allowlist/schema check; only rejects a malformed call before any network call is made.</summary>
public sealed class ApprovalBridgeValidationTests
{
    // ── action_id ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateActionId_RejectsBlank(string? actionId)
    {
        Assert.Equal("action_id is required", ApprovalBridgeValidation.ValidateActionId(actionId));
    }

    [Fact]
    public void ValidateActionId_RejectsOverLong()
    {
        var tooLong = new string('a', ApprovalBridgeValidation.MaxActionIdLength + 1);
        var err = ApprovalBridgeValidation.ValidateActionId(tooLong);
        Assert.NotNull(err);
        Assert.Contains("too long", err);
    }

    [Theory]
    [InlineData("garmin.oauth\t.exchange")]
    [InlineData("garmin.oauth\n.exchange")]
    public void ValidateActionId_RejectsControlCharacters(string actionId)
    {
        Assert.Equal("action_id contains control characters", ApprovalBridgeValidation.ValidateActionId(actionId));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("garmin\\oauth")]
    public void ValidateActionId_RejectsPathSeparators(string actionId)
    {
        Assert.Equal("action_id must not contain a path separator", ApprovalBridgeValidation.ValidateActionId(actionId));
    }

    [Fact]
    public void ValidateActionId_AcceptsARealisticDottedSlug()
    {
        Assert.Null(ApprovalBridgeValidation.ValidateActionId("garmin.oauth.exchange"));
    }

    // ── params ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateParamsJson_BlankNormalizesToEmptyObject(string? paramsJson)
    {
        var err = ApprovalBridgeValidation.ValidateParamsJson(paramsJson, out var normalized);
        Assert.Null(err);
        Assert.Equal("{}", normalized);
    }

    [Fact]
    public void ValidateParamsJson_AcceptsAWellFormedObjectAndPreservesIt()
    {
        const string json = """{ "auth_code": "abcd1234efgh" }""";
        var err = ApprovalBridgeValidation.ValidateParamsJson(json, out var normalized);
        Assert.Null(err);
        Assert.Equal(json, normalized);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{ broken")]
    public void ValidateParamsJson_RejectsUnparseableJson(string paramsJson)
    {
        var err = ApprovalBridgeValidation.ValidateParamsJson(paramsJson, out _);
        Assert.Equal("params is not valid JSON", err);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("true")]
    public void ValidateParamsJson_RejectsNonObjectTopLevel(string paramsJson)
    {
        var err = ApprovalBridgeValidation.ValidateParamsJson(paramsJson, out _);
        Assert.Equal("params must be a JSON object", err);
    }

    [Fact]
    public void ValidateParamsJson_RejectsOversizeInput()
    {
        var huge = "{\"x\":\"" + new string('a', ApprovalBridgeValidation.MaxParamsJsonLength) + "\"}";
        var err = ApprovalBridgeValidation.ValidateParamsJson(huge, out _);
        Assert.NotNull(err);
        Assert.Contains("too large", err);
    }
}
