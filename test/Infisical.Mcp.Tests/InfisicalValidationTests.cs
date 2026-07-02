using Xunit;

namespace Infisical.Mcp.Tests;

/// <summary>
/// Unit tests for <see cref="InfisicalValidation"/>: every tool parameter that flows into
/// an Infisical REST path is checked here BEFORE any HTTP request. These assert that valid
/// input passes (returns <c>null</c>) and that each rejection reason is produced for the
/// matching malformed input.
/// </summary>
public sealed class InfisicalValidationTests
{
    // ---- segment (group / service) ----
    [Theory]
    [InlineData("web")]
    [InlineData("api")]
    [InlineData("my-service")]
    [InlineData("svc_1")]
    public void ValidateSegment_AcceptsValid(string s) =>
        Assert.Null(InfisicalValidation.ValidateSegment(s, "group"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateSegment_RejectsEmpty_NamingTheParam(string? s) =>
        Assert.Equal("group is required", InfisicalValidation.ValidateSegment(s, "group"));

    [Fact]
    public void ValidateSegment_RejectsTooLong()
    {
        var s = new string('a', InfisicalValidation.MaxSegmentLength + 1);
        Assert.Contains("too long", InfisicalValidation.ValidateSegment(s, "service")!);
    }

    [Theory]
    [InlineData("bad\nname")]
    [InlineData("bad\tname")]
    [InlineData("bad\0name")]
    public void ValidateSegment_RejectsControlChars(string s) =>
        Assert.Contains("control characters", InfisicalValidation.ValidateSegment(s, "group")!);

    [Theory]
    [InlineData("a/b")]
    [InlineData("../etc")]
    [InlineData("a\\b")]
    public void ValidateSegment_RejectsPathSeparator(string s) =>
        Assert.Contains("path separator", InfisicalValidation.ValidateSegment(s, "group")!);

    [Fact]
    public void ValidateSegment_RejectsLeadingDash() =>
        Assert.Contains("must not start with '-'", InfisicalValidation.ValidateSegment("-flagish", "service")!);

    // ---- name ----
    [Theory]
    [InlineData("DB_PASSWORD")]
    [InlineData("NATS_NKEY_SEED")]
    [InlineData("api-key")]
    public void ValidateName_AcceptsValid(string n) =>
        Assert.Null(InfisicalValidation.ValidateName(n));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ValidateName_RejectsEmpty(string? n) =>
        Assert.Equal("name is required", InfisicalValidation.ValidateName(n));

    [Fact]
    public void ValidateName_RejectsTooLong()
    {
        var n = new string('K', InfisicalValidation.MaxNameLength + 1);
        Assert.Contains("too long", InfisicalValidation.ValidateName(n)!);
    }

    [Fact]
    public void ValidateName_RejectsControlChars() =>
        Assert.Contains("control characters", InfisicalValidation.ValidateName("bad\nkey")!);

    [Theory]
    [InlineData("a/b")]
    [InlineData("nested\\key")]
    public void ValidateName_RejectsPathSeparator(string n) =>
        Assert.Contains("path separator", InfisicalValidation.ValidateName(n)!);

    [Fact]
    public void ValidateName_RejectsLeadingDash() =>
        Assert.Contains("must not start with '-'", InfisicalValidation.ValidateName("-flag")!);

    // ---- value (caller-supplied secret) ----
    [Theory]
    [InlineData("tok-123")]
    [InlineData("a value with spaces and : = symbols")]  // free-form: any content allowed
    public void ValidateValue_AcceptsValid(string v) =>
        Assert.Null(InfisicalValidation.ValidateValue(v));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateValue_RejectsEmpty(string? v) =>
        Assert.Equal("value is required", InfisicalValidation.ValidateValue(v));

    [Fact]
    public void ValidateValue_RejectsTooLong()
    {
        var v = new string('x', InfisicalValidation.MaxValueLength + 1);
        Assert.Contains("too long", InfisicalValidation.ValidateValue(v)!);
    }

    // ---- random bytes ----
    [Theory]
    [InlineData(0)]     // non-positive is tolerated (tool defaults to 32) -> not rejected
    [InlineData(-5)]
    [InlineData(16)]
    [InlineData(32)]
    public void ValidateRandomBytes_AcceptsNonPositiveAndReasonable(int b) =>
        Assert.Null(InfisicalValidation.ValidateRandomBytes(b));

    [Fact]
    public void ValidateRandomBytes_RejectsAbsurd() =>
        Assert.Contains("out of range",
            InfisicalValidation.ValidateRandomBytes(InfisicalValidation.MaxRandomBytes + 1)!);
}
