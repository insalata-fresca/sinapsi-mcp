// ---------------------------------------------------------------------------
// IndexerValidationTests - the fail-fast input-validation surface. Every method
// returns null when valid else a human-readable reason; none throw.
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using Sinapsi.Indexer;
using Xunit;

namespace Sinapsi.Indexer.Tests;

/// <summary>
/// Pins <see cref="IndexerValidation"/>: each Validate* method is a pure guard
/// (null = ok, else a reason; never throws). These prove the caps, the
/// control-char/newline rejection, the closed <c>kind</c> set, and the
/// limit/tag bounds. NUL is written with the C# escape <c>\0</c>, never a
/// literal NUL byte, so this file stays plain text.
/// </summary>
public sealed class IndexerValidationTests
{
    // ── query (required) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateQuery_RejectsEmpty(string? q) =>
        Assert.Equal("query is required", IndexerValidation.ValidateQuery(q));

    [Fact]
    public void ValidateQuery_AcceptsAReasonableQuery() =>
        Assert.Null(IndexerValidation.ValidateQuery("hybrid search \"exact phrase\" OR fallback"));

    [Fact]
    public void ValidateQuery_RejectsOverLength()
    {
        var big = new string('x', IndexerValidation.MaxQueryLength + 1);
        Assert.Contains("too long", IndexerValidation.ValidateQuery(big));
    }

    [Theory]
    [InlineData("bad\0nul")]
    [InlineData("bad\nnewline")]
    [InlineData("tab\tinside")]
    public void ValidateQuery_RejectsControlChars(string q) =>
        Assert.Contains("control characters", IndexerValidation.ValidateQuery(q));

    // ── optional query (get_learning: null lists the scope) ──────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateOptionalQuery_AllowsAbsent(string? q) =>
        Assert.Null(IndexerValidation.ValidateOptionalQuery(q));

    [Fact]
    public void ValidateOptionalQuery_RejectsControlChars() =>
        Assert.Contains("control", IndexerValidation.ValidateOptionalQuery("x\0y"));

    // ── source / scope filter token ──────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("docs")]
    [InlineData("learnings")]
    public void ValidateFilterToken_AllowsAbsentOrPlain(string? v) =>
        Assert.Null(IndexerValidation.ValidateFilterToken("source", v));

    [Fact]
    public void ValidateFilterToken_RejectsOverLength_NamingTheParam()
    {
        var big = new string('a', IndexerValidation.MaxFilterLength + 1);
        var err = IndexerValidation.ValidateFilterToken("source", big);
        Assert.Contains("source", err);
        Assert.Contains("too long", err);
    }

    [Fact]
    public void ValidateFilterToken_RejectsControlChars_NamingTheParam() =>
        Assert.Contains("scope", IndexerValidation.ValidateFilterToken("scope", "a\nb"));

    // ── kind (closed set) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("doc")]
    [InlineData("pattern")]
    [InlineData("convention")]
    [InlineData("decision")]
    [InlineData("caveat")]
    [InlineData("scope")]
    [InlineData("state")]
    [InlineData("learning")]
    [InlineData("backlog")]
    public void ValidateKind_AcceptsAbsentOrKnown(string? kind) =>
        Assert.Null(IndexerValidation.ValidateKind(kind));

    [Theory]
    [InlineData("bogus")]
    [InlineData("Doc")]         // case-sensitive: classifier emits lowercase
    [InlineData("documents")]
    public void ValidateKind_RejectsUnknown(string kind) =>
        Assert.Contains("not one of", IndexerValidation.ValidateKind(kind));

    // ── limit bounds ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(30)]
    public void ValidateLimit_AcceptsInRange(int n) =>
        Assert.Null(IndexerValidation.ValidateLimit(n));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateLimit_RejectsNonPositive(int n) =>
        Assert.Contains("positive", IndexerValidation.ValidateLimit(n));

    [Fact]
    public void ValidateLimit_RejectsAbsurd() =>
        Assert.Contains("exceeds max", IndexerValidation.ValidateLimit(IndexerValidation.MaxLimit + 1));

    // ── slug / title / body / tags / session-context ─────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateSlug_RejectsEmpty(string? s) =>
        Assert.Equal("slug is required", IndexerValidation.ValidateSlug(s));

    [Fact]
    public void ValidateSlug_RejectsOverLength() =>
        Assert.Contains("too long", IndexerValidation.ValidateSlug(new string('a', IndexerValidation.MaxSlugLength + 1)));

    [Fact]
    public void ValidateTitle_RejectsEmpty() =>
        Assert.Equal("title is required", IndexerValidation.ValidateTitle("  "));

    [Fact]
    public void ValidateTitle_RejectsNewline_ItIsOneLine() =>
        Assert.Contains("one-line", IndexerValidation.ValidateTitle("line1\nline2"));

    [Fact]
    public void ValidateTitle_RejectsOverLength() =>
        Assert.Contains("too long", IndexerValidation.ValidateTitle(new string('t', IndexerValidation.MaxTitleLength + 1)));

    [Fact]
    public void ValidateBody_AllowsMarkdownNewlines() =>
        Assert.Null(IndexerValidation.ValidateBody("## Claim\n\nmulti\nline\tbody"));

    [Fact]
    public void ValidateBody_RejectsEmpty() =>
        Assert.Equal("body is required", IndexerValidation.ValidateBody(""));

    [Fact]
    public void ValidateBody_RejectsNulByte() =>
        Assert.Contains("control", IndexerValidation.ValidateBody("bad\0body"));

    [Fact]
    public void ValidateBody_RejectsOverLength() =>
        Assert.Contains("too long", IndexerValidation.ValidateBody(new string('b', IndexerValidation.MaxBodyLength + 1)));

    [Fact]
    public void ValidateTags_AllowsNullOrEmpty()
    {
        Assert.Null(IndexerValidation.ValidateTags(null));
        Assert.Null(IndexerValidation.ValidateTags(Array.Empty<string>()));
    }

    [Fact]
    public void ValidateTags_RejectsTooMany() =>
        Assert.Contains("too many tags", IndexerValidation.ValidateTags(
            Enumerable.Repeat("t", IndexerValidation.MaxTags + 1).ToArray()));

    [Fact]
    public void ValidateTags_RejectsControlCharInATag() =>
        Assert.Contains("control", IndexerValidation.ValidateTags(new[] { "ok", "bad\0tag" }));

    [Fact]
    public void ValidateSessionContext_AllowsAbsent() =>
        Assert.Null(IndexerValidation.ValidateSessionContext(null));

    [Fact]
    public void ValidateSessionContext_RejectsNewline() =>
        Assert.Contains("one-line", IndexerValidation.ValidateSessionContext("a\nb"));
}
