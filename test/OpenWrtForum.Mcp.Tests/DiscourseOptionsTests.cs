using System.Globalization;
using OpenWrtForum.Mcp;
using Xunit;

namespace OpenWrtForum.Mcp.Tests;

/// <summary>
/// <see cref="DiscourseOptions.FromEnvironment"/>: the config is fully env-driven
/// and nothing site-specific is baked in. These tests pin that contract — the
/// neutral defaults, the URL override + trailing-slash normalisation, the
/// read-only-vs-write credential gate, and the fail-closed HTTP-timeout clamp
/// (bad config throws naming the offending env var rather than being silently
/// honoured). They mutate process env, so they run serially.
/// </summary>
[Collection("env")]
public sealed class DiscourseOptionsTests : IDisposable
{
    private static readonly string[] Vars =
    {
        "DISCOURSE_URL", "DISCOURSE_API_USERNAME", "DISCOURSE_API_PASSWORD",
        "DISCOURSE_HTTP_TIMEOUT_MS",
    };

    public DiscourseOptionsTests() => ClearEnv();
    public void Dispose() => ClearEnv();
    private static void ClearEnv() { foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null); }

    // ── defaults + env binding ───────────────────────────────────────────────
    [Fact]
    public void Default_Url_When_Env_Unset()
    {
        var o = DiscourseOptions.FromEnvironment();
        Assert.Equal("https://forum.openwrt.org", o.Url);
    }

    [Fact]
    public void DefaultsAreNeutral_AndNotSiteSpecific()
    {
        var o = DiscourseOptions.FromEnvironment();
        Assert.Equal("https://forum.openwrt.org", o.Url);
        Assert.Equal("", o.Username);
        Assert.Equal("", o.Password);
        Assert.Equal(30_000, o.HttpTimeoutMs);
        Assert.False(o.HasCredentials);
    }

    [Fact]
    public void Url_Is_Env_Driven()
    {
        Environment.SetEnvironmentVariable("DISCOURSE_URL", "https://forum.example.com");
        var o = DiscourseOptions.FromEnvironment();
        Assert.Equal("https://forum.example.com", o.Url);
    }

    [Fact]
    public void Trailing_Slash_Is_Stripped()
    {
        Environment.SetEnvironmentVariable("DISCOURSE_URL", "https://forum.example.com/");
        var o = DiscourseOptions.FromEnvironment();
        Assert.Equal("https://forum.example.com", o.Url);
    }

    [Fact]
    public void No_Credentials_Means_ReadOnly()
    {
        var o = DiscourseOptions.FromEnvironment();
        Assert.False(o.HasCredentials);
    }

    [Fact]
    public void Both_Credentials_Required_For_Write()
    {
        Assert.False(new DiscourseOptions("https://x", "user", "", 30_000).HasCredentials);
        Assert.False(new DiscourseOptions("https://x", "", "pass", 30_000).HasCredentials);
        Assert.True(new DiscourseOptions("https://x", "user", "pass", 30_000).HasCredentials);
    }

    [Fact]
    public void Credentials_Are_Env_Driven()
    {
        Environment.SetEnvironmentVariable("DISCOURSE_API_USERNAME", "alice");
        Environment.SetEnvironmentVariable("DISCOURSE_API_PASSWORD", "s3cret");
        var o = DiscourseOptions.FromEnvironment();
        Assert.Equal("alice", o.Username);
        Assert.Equal("s3cret", o.Password);
        Assert.True(o.HasCredentials);
    }

    [Fact]
    public void EveryVar_IsBound()
    {
        Environment.SetEnvironmentVariable("DISCOURSE_URL", "https://other.example.org");
        Environment.SetEnvironmentVariable("DISCOURSE_API_USERNAME", "bob");
        Environment.SetEnvironmentVariable("DISCOURSE_API_PASSWORD", "pw");
        Environment.SetEnvironmentVariable("DISCOURSE_HTTP_TIMEOUT_MS", "12345");

        var o = DiscourseOptions.FromEnvironment();

        Assert.Equal("https://other.example.org", o.Url);
        Assert.Equal("bob", o.Username);
        Assert.Equal("pw", o.Password);
        Assert.Equal(12345, o.HttpTimeoutMs);
    }

    // ── timeout clamp (fail-closed on bad config) ────────────────────────────
    // 0 would be a footgun HttpClient.Timeout; a negative value throws inside the
    // setter; a non-numeric value cannot be honoured. All are rejected as invalid
    // config with a clear error naming the offending env var, rather than being
    // silently honoured or crashing deep in the HTTP path.
    [Fact]
    public void DefaultTimeout_When_Env_Unset()
    {
        Assert.Equal(30_000, DiscourseOptions.FromEnvironment().HttpTimeoutMs);
    }

    [Fact]
    public void ValidTimeout_IsHonoured()
    {
        Environment.SetEnvironmentVariable("DISCOURSE_HTTP_TIMEOUT_MS", "5000");
        Assert.Equal(5000, DiscourseOptions.FromEnvironment().HttpTimeoutMs);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-30000")]
    [InlineData("abc")]
    [InlineData("12.5")]
    [InlineData("")]      // set-but-empty is treated as unset → default, NOT a throw
    public void BadTimeout_IsRejected_OrDefaulted(string bad)
    {
        Environment.SetEnvironmentVariable("DISCOURSE_HTTP_TIMEOUT_MS", bad);

        if (bad.Length == 0)
        {
            // An empty value is indistinguishable from unset → falls back to the default.
            Assert.Equal(30_000, DiscourseOptions.FromEnvironment().HttpTimeoutMs);
            return;
        }

        var ex = Assert.Throws<InvalidOperationException>(() => DiscourseOptions.FromEnvironment());
        Assert.Contains("DISCOURSE_HTTP_TIMEOUT_MS", ex.Message);
    }

    [Fact]
    public void NonNumericTimeout_NamesTheVar()
    {
        Environment.SetEnvironmentVariable("DISCOURSE_HTTP_TIMEOUT_MS", "not-a-number");
        var ex = Assert.Throws<InvalidOperationException>(() => DiscourseOptions.FromEnvironment());
        Assert.Contains("DISCOURSE_HTTP_TIMEOUT_MS", ex.Message);
    }

    [Fact]
    public void AbsurdlyLargeTimeout_IsRejected()
    {
        Environment.SetEnvironmentVariable(
            "DISCOURSE_HTTP_TIMEOUT_MS",
            (DiscourseOptions.MaxHttpTimeoutMs + 1).ToString(CultureInfo.InvariantCulture));

        var ex = Assert.Throws<InvalidOperationException>(() => DiscourseOptions.FromEnvironment());
        Assert.Contains("DISCOURSE_HTTP_TIMEOUT_MS", ex.Message);
    }

    [Fact]
    public void MaxTimeout_IsAccepted_AtTheCeiling()
    {
        Environment.SetEnvironmentVariable(
            "DISCOURSE_HTTP_TIMEOUT_MS",
            DiscourseOptions.MaxHttpTimeoutMs.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(DiscourseOptions.MaxHttpTimeoutMs, DiscourseOptions.FromEnvironment().HttpTimeoutMs);
    }
}
