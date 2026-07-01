using Xunit;
using Zitadel.Mcp;

namespace Zitadel.Mcp.Tests;

/// <summary>
/// Unit tests for <see cref="ZitadelValidation"/>: every tool parameter is checked here BEFORE
/// any HTTP call is issued. These assert that valid input passes (returns <c>null</c>) and that
/// each rejection reason is produced for the matching malformed input.
/// </summary>
public sealed class ZitadelValidationTests
{
    // ── ids ───────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("123456789012345678")]
    [InlineData("proj-1")]
    [InlineData("a")]
    public void ValidateId_AcceptsValid(string id) =>
        Assert.Null(ZitadelValidation.ValidateId("userId", id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateId_RejectsEmpty(string? id) =>
        Assert.Equal("userId is required", ZitadelValidation.ValidateId("userId", id));

    [Fact]
    public void ValidateId_RejectsTooLong()
    {
        var id = new string('9', ZitadelValidation.MaxIdLength + 1);
        Assert.Contains("too long", ZitadelValidation.ValidateId("userId", id)!);
    }

    [Fact]
    public void ValidateId_RejectsControlChars() =>
        Assert.Contains("control characters", ZitadelValidation.ValidateId("userId", "12\n34")!);

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("../x")]
    public void ValidateId_RejectsPathSeparators(string id) =>
        Assert.Contains("path separator", ZitadelValidation.ValidateId("userId", id)!);

    // ── names ─────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("My Project")]
    [InlineData("svc-account")]
    public void ValidateName_AcceptsValid(string name) =>
        Assert.Null(ZitadelValidation.ValidateName("name", name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ValidateName_RejectsEmpty(string? name) =>
        Assert.Equal("name is required", ZitadelValidation.ValidateName("name", name));

    [Fact]
    public void ValidateName_RejectsTooLong()
    {
        var name = new string('a', ZitadelValidation.MaxNameLength + 1);
        Assert.Contains("too long", ZitadelValidation.ValidateName("name", name)!);
    }

    [Fact]
    public void ValidateName_RejectsControlChars() =>
        Assert.Contains("control characters", ZitadelValidation.ValidateName("name", "bad\tname")!);

    // ── description (optional) ─────────────────────────────────────────────────
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a normal description")]
    public void ValidateDescription_AcceptsValid(string? d) =>
        Assert.Null(ZitadelValidation.ValidateDescription(d));

    [Fact]
    public void ValidateDescription_RejectsTooLong()
    {
        var d = new string('x', ZitadelValidation.MaxDescriptionLength + 1);
        Assert.Contains("too long", ZitadelValidation.ValidateDescription(d)!);
    }

    [Fact]
    public void ValidateDescription_RejectsControlChars() =>
        Assert.Contains("control characters", ZitadelValidation.ValidateDescription("bad\ndesc")!);

    // ── enum tokens ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(null)]                       // null = omitted / default substituted
    [InlineData("OIDC_APP_TYPE_WEB")]
    [InlineData("ACCESS_TOKEN_TYPE_JWT")]
    public void ValidateEnum_AcceptsValid(string? v) =>
        Assert.Null(ZitadelValidation.ValidateEnum("appType", v));

    [Fact]
    public void ValidateEnum_RejectsEmpty() =>
        Assert.Contains("must not be empty", ZitadelValidation.ValidateEnum("appType", "")!);

    [Fact]
    public void ValidateEnum_RejectsControlChars() =>
        Assert.Contains("control characters", ZitadelValidation.ValidateEnum("appType", "A\nB")!);

    [Fact]
    public void ValidateEnum_RejectsLeadingDash() =>
        Assert.Contains("must not start with '-'", ZitadelValidation.ValidateEnum("appType", "-flagish")!);

    [Fact]
    public void ValidateEnumList_AcceptsNullOrValid()
    {
        Assert.Null(ZitadelValidation.ValidateEnumList("responseTypes", null));
        Assert.Null(ZitadelValidation.ValidateEnumList("responseTypes", new[] { "OIDC_RESPONSE_TYPE_CODE" }));
    }

    [Fact]
    public void ValidateEnumList_RejectsBadEntry() =>
        Assert.Contains("control characters",
            ZitadelValidation.ValidateEnumList("responseTypes", new[] { "OK", "bad\nentry" })!);

    // ── uris ──────────────────────────────────────────────────────────────────
    [Fact]
    public void ValidateUris_RequiredRejectsNullOrEmpty()
    {
        Assert.Equal("redirectUris is required", ZitadelValidation.ValidateUris("redirectUris", null, required: true));
        Assert.Equal("redirectUris is required", ZitadelValidation.ValidateUris("redirectUris", Array.Empty<string>(), required: true));
    }

    [Fact]
    public void ValidateUris_OptionalAcceptsNull() =>
        Assert.Null(ZitadelValidation.ValidateUris("redirectUris", null, required: false));

    [Fact]
    public void ValidateUris_AcceptsValidList() =>
        Assert.Null(ZitadelValidation.ValidateUris(
            "redirectUris", new[] { "https://app.example.com/cb", "https://app.example.com/cb2" }, required: true));

    [Fact]
    public void ValidateUris_RejectsTooMany()
    {
        var many = Enumerable.Range(0, ZitadelValidation.MaxUris + 1).Select(i => $"https://x/{i}").ToArray();
        Assert.Contains("too many", ZitadelValidation.ValidateUris("redirectUris", many, required: true)!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ValidateUris_RejectsEmptyEntry(string bad) =>
        Assert.Contains("is empty", ZitadelValidation.ValidateUris("redirectUris", new[] { "https://ok", bad }, required: true)!);

    [Fact]
    public void ValidateUris_RejectsControlCharsEntry() =>
        Assert.Contains("control characters",
            ZitadelValidation.ValidateUris("redirectUris", new[] { "https://ok\n" }, required: true)!);

    // ── expiration ─────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("2099-01-01T00:00:00Z")]
    [InlineData("2030-06-15T12:30:00+02:00")]
    public void ValidateExpiration_AcceptsValid(string iso) =>
        Assert.Null(ZitadelValidation.ValidateExpiration(iso));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ValidateExpiration_RejectsEmpty(string? iso) =>
        Assert.Equal("expiration is required", ZitadelValidation.ValidateExpiration(iso));

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2099-13-40")]
    public void ValidateExpiration_RejectsUnparseable(string iso) =>
        Assert.Contains("not a valid", ZitadelValidation.ValidateExpiration(iso)!);

    [Fact]
    public void ValidateExpiration_RejectsControlChars() =>
        Assert.Contains("control characters", ZitadelValidation.ValidateExpiration("2099-01-01T00:00:00Z\n")!);

    // ── limit ──────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1000)]
    public void ValidateLimit_AcceptsInRange(int limit) =>
        Assert.Null(ZitadelValidation.ValidateLimit(limit));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void ValidateLimit_RejectsOutOfRange(int limit) =>
        Assert.Contains("out of range", ZitadelValidation.ValidateLimit(limit)!);

    // ── agent_file basename ─────────────────────────────────────────────────────
    [Theory]
    [InlineData("agent-journey-ux")]
    [InlineData("agent.json.bak")]
    public void ValidateAgentFile_AcceptsSafeBasename(string name) =>
        Assert.Null(ZitadelValidation.ValidateAgentFile(name));

    [Theory]
    [InlineData(null, "is required")]
    [InlineData("", "is required")]
    [InlineData("../etc/passwd", "bare basename")]
    [InlineData("a/b", "bare basename")]
    [InlineData("a\\b", "bare basename")]
    public void ValidateAgentFile_RejectsUnsafe(string? name, string fragment) =>
        Assert.Contains(fragment, ZitadelValidation.ValidateAgentFile(name)!);

    [Theory]
    [InlineData("agent-journey-ux", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    public void IsSafeBasename_MatchesExpected(string name, bool expected) =>
        Assert.Equal(expected, ZitadelValidation.IsSafeBasename(name));
}
