using Xunit;

namespace StepCa.Mcp.Tests;

/// <summary>
/// Unit tests for <see cref="StepCaValidation"/>: every mutating-tool parameter
/// is checked here BEFORE a subprocess is spawned. These assert that valid input
/// passes (returns <c>null</c>) and that each rejection reason is produced for
/// the matching malformed input.
/// </summary>
public sealed class StepCaValidationTests
{
    // ── common_name ─────────────────────────────────────────────────────────
    [Theory]
    [InlineData("node.example.com")]
    [InlineData("a")]
    [InlineData("My Service")]            // free-form CN with a space is allowed
    public void ValidateCommonName_AcceptsValid(string cn) =>
        Assert.Null(StepCaValidation.ValidateCommonName(cn));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateCommonName_RejectsEmpty(string? cn) =>
        Assert.Equal("common_name is required", StepCaValidation.ValidateCommonName(cn));

    [Fact]
    public void ValidateCommonName_RejectsTooLong()
    {
        var cn = new string('a', StepCaValidation.MaxNameLength + 1);
        Assert.Contains("too long", StepCaValidation.ValidateCommonName(cn));
    }

    [Fact]
    public void ValidateCommonName_RejectsControlChars() =>
        Assert.Contains("control characters", StepCaValidation.ValidateCommonName("bad\nname")!);

    [Fact]
    public void ValidateCommonName_RejectsLeadingDash() =>
        Assert.Contains("must not start with '-'", StepCaValidation.ValidateCommonName("--ca-url")!);

    // ── sans ────────────────────────────────────────────────────────────────
    [Fact]
    public void ValidateSans_AcceptsNullOrEmpty()
    {
        Assert.Null(StepCaValidation.ValidateSans(null));
        Assert.Null(StepCaValidation.ValidateSans(Array.Empty<string>()));
    }

    [Fact]
    public void ValidateSans_AcceptsValidList() =>
        Assert.Null(StepCaValidation.ValidateSans(new[] { "a.example.com", "10.0.0.1" }));

    [Fact]
    public void ValidateSans_RejectsTooMany()
    {
        var many = Enumerable.Range(0, StepCaValidation.MaxSans + 1).Select(i => $"s{i}.example.com").ToArray();
        Assert.Contains("too many SANs", StepCaValidation.ValidateSans(many));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ValidateSans_RejectsEmptyEntry(string bad) =>
        Assert.Contains("is empty", StepCaValidation.ValidateSans(new[] { "ok.example.com", bad })!);

    [Fact]
    public void ValidateSans_RejectsControlCharsEntry() =>
        Assert.Contains("control characters", StepCaValidation.ValidateSans(new[] { "bad\r.example.com" })!);

    [Fact]
    public void ValidateSans_RejectsLeadingDashEntry() =>
        Assert.Contains("must not start with '-'", StepCaValidation.ValidateSans(new[] { "-flagish" })!);

    // ── serial_number ───────────────────────────────────────────────────────
    [Theory]
    [InlineData("123456789")]
    [InlineData("0")]
    [InlineData("0xDEADBEEF")]
    [InlineData("0xdeadbeef")]
    [InlineData("  42  ")]                 // surrounding whitespace is trimmed
    public void ValidateSerialNumber_AcceptsValid(string serial) =>
        Assert.Null(StepCaValidation.ValidateSerialNumber(serial));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateSerialNumber_RejectsEmptyWithExactMessage(string? serial) =>
        Assert.Equal("serial_number is required", StepCaValidation.ValidateSerialNumber(serial));

    [Theory]
    [InlineData("-5")]
    [InlineData("12ab")]                   // decimal with stray hex
    [InlineData("0x")]                     // empty hex
    [InlineData("0xZZ")]                   // non-hex
    [InlineData("12.5")]
    public void ValidateSerialNumber_RejectsMalformed(string serial) =>
        Assert.NotNull(StepCaValidation.ValidateSerialNumber(serial));

    [Fact]
    public void ValidateSerialNumber_RejectsTooLong()
    {
        var serial = new string('9', StepCaValidation.MaxSerialLength + 1);
        Assert.Contains("too long", StepCaValidation.ValidateSerialNumber(serial));
    }

    // ── reason_code ─────────────────────────────────────────────────────────
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    public void ValidateReasonCode_AcceptsInRange(int code) =>
        Assert.Null(StepCaValidation.ValidateReasonCode(code));

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(255)]
    public void ValidateReasonCode_RejectsOutOfRange(int code) =>
        Assert.Contains("out of range", StepCaValidation.ValidateReasonCode(code)!);
}
