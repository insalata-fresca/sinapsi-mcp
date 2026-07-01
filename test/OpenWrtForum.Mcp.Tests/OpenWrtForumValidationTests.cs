using OpenWrtForum.Mcp;
using Xunit;

// Plain-ASCII banners keep this file a clean TEXT diff. NUL inputs are written
// with the C# escape \0 (never a literal NUL byte) so the source stays textual.

namespace OpenWrtForum.Mcp.Tests;

/// <summary>
/// Unit tests for <see cref="OpenWrtForumValidation"/>: every tool parameter that
/// flows into an outbound HTTP request is checked here BEFORE any HTTP call. These
/// assert that valid input passes (returns <c>null</c>) and that each rejection
/// reason is produced for the matching malformed input.
/// </summary>
public sealed class OpenWrtForumValidationTests
{
    // ── query ────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("ath12k #devel")]
    [InlineData("QCN9274 order:latest")]
    [InlineData("@someuser")]
    [InlineData("a")]
    public void ValidateQuery_AcceptsValid(string query) =>
        Assert.Null(OpenWrtForumValidation.ValidateQuery(query));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateQuery_RejectsEmpty(string? query) =>
        Assert.Equal("query is required", OpenWrtForumValidation.ValidateQuery(query));

    [Fact]
    public void ValidateQuery_RejectsTooLong()
    {
        var q = new string('a', OpenWrtForumValidation.MaxQueryLength + 1);
        Assert.Contains("too long", OpenWrtForumValidation.ValidateQuery(q)!);
    }

    [Theory]
    [InlineData("bad\nquery")]
    [InlineData("bad\tquery")]
    [InlineData("bad\rquery")]
    [InlineData("nul\0byte")]   // \0 is the C# NUL escape, NOT a literal NUL byte
    public void ValidateQuery_RejectsControlChars(string query) =>
        Assert.Contains("control characters", OpenWrtForumValidation.ValidateQuery(query)!);

    // ── page ─────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    public void ValidatePage_AcceptsInRange(int page) =>
        Assert.Null(OpenWrtForumValidation.ValidatePage(page));

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(1001)]
    [InlineData(int.MaxValue)]
    public void ValidatePage_RejectsOutOfRange(int page) =>
        Assert.Contains("out of range", OpenWrtForumValidation.ValidatePage(page)!);

    // ── id ─────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(int.MaxValue)]
    public void ValidateId_AcceptsPositive(int id) =>
        Assert.Null(OpenWrtForumValidation.ValidateId(id, "topic_id"));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ValidateId_RejectsNonPositive_NamingTheParam(int id)
    {
        var msg = OpenWrtForumValidation.ValidateId(id, "category_id");
        Assert.Contains("category_id", msg!);
        Assert.Contains("positive integer", msg!);
    }

    // ── category_slug (URL path segment) ─────────────────────────────────────
    [Theory]
    [InlineData(null)]      // absent slug is fine (falls back to site-wide latest)
    [InlineData("")]
    [InlineData("devel")]
    [InlineData("for-developers")]
    public void ValidateCategorySlug_AcceptsValidOrAbsent(string? slug) =>
        Assert.Null(OpenWrtForumValidation.ValidateCategorySlug(slug));

    [Fact]
    public void ValidateCategorySlug_RejectsTooLong()
    {
        var slug = new string('s', OpenWrtForumValidation.MaxSlugLength + 1);
        Assert.Contains("too long", OpenWrtForumValidation.ValidateCategorySlug(slug)!);
    }

    [Theory]
    [InlineData("bad\nslug")]
    [InlineData("nul\0slug")]   // C# NUL escape
    public void ValidateCategorySlug_RejectsControlChars(string slug) =>
        Assert.Contains("control characters", OpenWrtForumValidation.ValidateCategorySlug(slug)!);

    [Fact]
    public void ValidateCategorySlug_RejectsLeadingDash() =>
        Assert.Contains("must not start with '-'",
            OpenWrtForumValidation.ValidateCategorySlug("-l")!);   // could masquerade as a flag

    [Theory]
    [InlineData("a/b")]
    [InlineData("../etc")]
    [InlineData("a\\b")]
    public void ValidateCategorySlug_RejectsPathSeparators(string slug) =>
        Assert.Contains("path separator", OpenWrtForumValidation.ValidateCategorySlug(slug)!);

    // ── title ────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("A sensible topic title")]
    [InlineData("x")]
    public void ValidateTitle_AcceptsValid(string title) =>
        Assert.Null(OpenWrtForumValidation.ValidateTitle(title));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateTitle_RejectsEmpty(string? title) =>
        Assert.Equal("title is required", OpenWrtForumValidation.ValidateTitle(title));

    [Fact]
    public void ValidateTitle_RejectsTooLong()
    {
        var t = new string('t', OpenWrtForumValidation.MaxTitleLength + 1);
        Assert.Contains("too long", OpenWrtForumValidation.ValidateTitle(t)!);
    }

    [Theory]
    [InlineData("bad\ntitle")]
    [InlineData("nul\0title")]   // C# NUL escape
    public void ValidateTitle_RejectsControlChars(string title) =>
        Assert.Contains("control characters", OpenWrtForumValidation.ValidateTitle(title)!);

    // ── body (multi-line markdown) ───────────────────────────────────────────
    [Theory]
    [InlineData("Just a body.")]
    [InlineData("Line one\nLine two\n\n- a bullet")]   // newlines are legitimate
    [InlineData("tab\there")]                            // tabs are legitimate
    public void ValidateBody_AcceptsValidIncludingNewlines(string body) =>
        Assert.Null(OpenWrtForumValidation.ValidateBody(body));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateBody_RejectsEmpty(string? body) =>
        Assert.Equal("body is required", OpenWrtForumValidation.ValidateBody(body));

    [Fact]
    public void ValidateBody_RejectsTooLong()
    {
        var b = new string('b', OpenWrtForumValidation.MaxBodyLength + 1);
        Assert.Contains("too long", OpenWrtForumValidation.ValidateBody(b)!);
    }

    [Theory]
    [InlineData("nul\0body")]   // NUL is never legitimate, even in a body
    [InlineData("bell\abody")]  // \a (BEL) is a disallowed C0 control
    public void ValidateBody_RejectsDisallowedControl(string body) =>
        Assert.Contains("control characters", OpenWrtForumValidation.ValidateBody(body)!);

    // ── tags ─────────────────────────────────────────────────────────────────
    [Fact]
    public void ValidateTags_AcceptsNullOrEmpty()
    {
        Assert.Null(OpenWrtForumValidation.ValidateTags(null));
        Assert.Null(OpenWrtForumValidation.ValidateTags(Array.Empty<string>()));
    }

    [Fact]
    public void ValidateTags_AcceptsValidList() =>
        Assert.Null(OpenWrtForumValidation.ValidateTags(new[] { "ath12k", "qcn9274" }));

    [Fact]
    public void ValidateTags_RejectsTooMany()
    {
        var many = Enumerable.Range(0, OpenWrtForumValidation.MaxTags + 1).Select(i => $"t{i}").ToArray();
        Assert.Contains("too many tags", OpenWrtForumValidation.ValidateTags(many)!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ValidateTags_RejectsEmptyEntry(string bad) =>
        Assert.Contains("is empty", OpenWrtForumValidation.ValidateTags(new[] { "ok", bad })!);

    [Fact]
    public void ValidateTags_RejectsTooLongEntry()
    {
        var big = new string('x', OpenWrtForumValidation.MaxTagLength + 1);
        Assert.Contains("too long", OpenWrtForumValidation.ValidateTags(new[] { big })!);
    }

    [Theory]
    [InlineData("bad\ntag")]
    [InlineData("nul\0tag")]   // C# NUL escape
    public void ValidateTags_RejectsControlCharsEntry(string bad) =>
        Assert.Contains("control characters", OpenWrtForumValidation.ValidateTags(new[] { bad })!);

    // ── notification filter ──────────────────────────────────────────────────
    [Theory]
    [InlineData(null)]      // absent → tool treats as "all"
    [InlineData("")]
    [InlineData("all")]
    [InlineData("unread")]
    public void ValidateNotificationFilter_AcceptsValidOrAbsent(string? filter) =>
        Assert.Null(OpenWrtForumValidation.ValidateNotificationFilter(filter));

    [Theory]
    [InlineData("ALL")]     // case-sensitive: only the exact tokens are accepted
    [InlineData("read")]
    [InlineData("everything")]
    public void ValidateNotificationFilter_RejectsUnknown(string filter) =>
        Assert.Equal("filter must be 'all' or 'unread'",
            OpenWrtForumValidation.ValidateNotificationFilter(filter));
}
