using Xunit;

namespace ApprovalBridge.Mcp.Tests;

public sealed class ApprovalBridgeErrorsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_ReturnsASentinel_ForBlankInput(string? input)
    {
        Assert.Equal("approval bridge request failed with no diagnostic output", ApprovalBridgeErrors.Sanitize(input));
    }

    [Fact]
    public void Sanitize_RedactsATokenAssignment_ButKeepsTheKeyName()
    {
        var scrubbed = ApprovalBridgeErrors.Sanitize("connection refused: token=abc123xyz");
        Assert.Contains("token=", scrubbed);
        Assert.Contains("[redacted]", scrubbed);
        Assert.DoesNotContain("abc123xyz", scrubbed);
    }

    [Fact]
    public void Sanitize_CapsLengthAndMarksTruncation()
    {
        var huge = new string('x', ApprovalBridgeErrors.MaxErrorLength + 500);
        var scrubbed = ApprovalBridgeErrors.Sanitize(huge);
        Assert.True(scrubbed.Length <= ApprovalBridgeErrors.MaxErrorLength + "… [truncated]".Length);
        Assert.EndsWith("[truncated]", scrubbed);
    }

    [Fact]
    public void Sanitize_LeavesAnOrdinaryMessageUntouched()
    {
        Assert.Equal("connection timed out", ApprovalBridgeErrors.Sanitize("connection timed out"));
    }
}
