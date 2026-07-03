using Bridge.Mcp.Auth;
using Bridge.Mcp.Git;
using Xunit;

namespace Bridge.Mcp.Tests;

/// <summary>
/// Parity tests for slugify + sha8, derived from the Python reference implementation.
///
/// Python slugify: _SLUG_RE.sub("-", text.lower()).strip("-")[:64] or "untitled"
///   where _SLUG_RE = re.compile(r"[^a-z0-9]+")
///
/// Python sha8:    hashlib.sha256(text.encode("utf-8")).hexdigest()[:8]
///
/// Fixture values confirmed by running the Python functions directly.
/// These tests guard byte-exact parity so the C# deposit filename dedup key
/// matches the Python-generated filename.
/// </summary>
public sealed class GitOpsParityTests
{
    // ── slugify parity ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Hello World", "hello-world")]
    [InlineData("Hello, World!", "hello-world")]
    [InlineData("My Session 2024-07-03", "my-session-2024-07-03")]
    [InlineData("  leading and trailing  ", "leading-and-trailing")]
    [InlineData("ALL CAPS TITLE", "all-caps-title")]
    [InlineData("multiple---dashes", "multiple-dashes")]
    [InlineData("abc!@#$%^&*()def", "abc-def")]
    [InlineData("", "untitled")]
    [InlineData("   ", "untitled")]
    [InlineData("123numeric", "123numeric")]
    [InlineData("camelCaseTitle", "camelcasetitle")]
    [InlineData("über-café", "ber-caf")]  // non-ASCII stripped by [^a-z0-9]+
    // Unicode full-lowercasing divergence cases (U+0130 İ, U+212A K):
    // Python str.lower() maps İ (U+0130) -> "i" + COMBINING DOT ABOVE (U+0307).
    // The combining dot is non-[a-z0-9] so the regex inserts a "-", giving "i-stanbul".
    // C# ToLowerInvariant() left U+0130 unchanged (bug, now fixed by pre-substitution).
    [InlineData("İstanbul",      "i-stanbul")]   // U+0130 I WITH DOT ABOVE — the confirmed regression case
    [InlineData("İ",             "i")]            // U+0130 alone -> "i" (combining dot stripped, no separator needed)
    [InlineData("title İstanbul", "title-i-stanbul")] // U+0130 mid-string
    [InlineData("ẞtraße",        "tra-e")]        // U+1E9E ẞ -> "ß" in Python (non-ASCII, stripped); ß -> stripped too
    public void Slugify_MatchesPython(string input, string expected)
    {
        Assert.Equal(expected, GitOpsService.Slugify(input));
    }

    [Fact]
    public void Slugify_TruncatesAt64Chars()
    {
        var long70 = new string('a', 70);
        var slug = GitOpsService.Slugify(long70);
        Assert.Equal(64, slug.Length);
        Assert.Equal(new string('a', 64), slug);
    }

    [Fact]
    public void Slugify_FallbackUntitled_WhenOnlySpecialChars()
    {
        Assert.Equal("untitled", GitOpsService.Slugify("!@#$%^&*()"));
    }

    [Fact]
    public void Slugify_StripsDashesFromBothEnds()
    {
        // "---hello---" → lower → "---hello---" → sub → "-hello-" → strip → "hello"
        Assert.Equal("hello", GitOpsService.Slugify("---hello---"));
    }

    // ── sha8 parity ───────────────────────────────────────────────────────────
    // Python: hashlib.sha256(text.encode("utf-8")).hexdigest()[:8]
    // Confirmed with: import hashlib; hashlib.sha256(b"Hello World").hexdigest()[:8]

    [Theory]
    [InlineData("Hello World",    "a591a6d4")]
    [InlineData("",               "e3b0c442")]
    [InlineData("bridge-mcp",     "05f90003")]
    [InlineData("test content",   "6ae8a755")]
    [InlineData("abc",            "ba7816bf")]
    public void Sha8_MatchesPython(string input, string expected)
    {
        Assert.Equal(expected, GitOpsService.Sha8(input));
    }

    [Fact]
    public void Sha8_IsAlwaysEightCharsLowerHex()
    {
        var h = GitOpsService.Sha8("any content here");
        Assert.Equal(8, h.Length);
        Assert.All(h, c => Assert.True(char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c)));
    }

    // ── safe artifact path parity ─────────────────────────────────────────────

    [Fact]
    public void SafeArtifactPath_StripLeadingSlash()
    {
        Assert.Equal("foo/bar.md", GitOpsService.SafeArtifactPath("/foo/bar.md"));
    }

    [Fact]
    public void SafeArtifactPath_RejectsTraversal()
    {
        var ex = Assert.Throws<BridgeToolException>(() =>
            GitOpsService.SafeArtifactPath("foo/../secret.md"));
        Assert.Equal("invalid_path", ex.Code);
    }

    [Fact]
    public void SafeArtifactPath_RejectsEmpty()
    {
        var ex = Assert.Throws<BridgeToolException>(() =>
            GitOpsService.SafeArtifactPath(""));
        Assert.Equal("invalid_path", ex.Code);
    }

    [Fact]
    public void SafeArtifactPath_RejectsTooLong()
    {
        var ex = Assert.Throws<BridgeToolException>(() =>
            GitOpsService.SafeArtifactPath(new string('a', 257)));
        Assert.Equal("invalid_path", ex.Code);
    }

    [Fact]
    public void SafeArtifactPath_AcceptsNested()
    {
        Assert.Equal("images/photo.jpg", GitOpsService.SafeArtifactPath("images/photo.jpg"));
    }
}
