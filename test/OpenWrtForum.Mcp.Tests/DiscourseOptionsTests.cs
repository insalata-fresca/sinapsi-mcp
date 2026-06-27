using OpenWrtForum.Mcp;
using Xunit;

namespace OpenWrtForum.Mcp.Tests;

/// <summary>
/// The config is fully env-driven and the URL is never baked in. These tests
/// pin that contract: the default, the override, trailing-slash normalisation,
/// and the read-only-vs-write credential gate.
/// </summary>
[Collection("env")]
public sealed class DiscourseOptionsTests
{
    private static void ClearEnv()
    {
        Environment.SetEnvironmentVariable("DISCOURSE_URL", null);
        Environment.SetEnvironmentVariable("DISCOURSE_API_USERNAME", null);
        Environment.SetEnvironmentVariable("DISCOURSE_API_PASSWORD", null);
    }

    [Fact]
    public void Default_Url_When_Env_Unset()
    {
        ClearEnv();
        var o = DiscourseOptions.FromEnvironment();
        Assert.Equal("https://forum.openwrt.org", o.Url);
    }

    [Fact]
    public void Url_Is_Env_Driven()
    {
        ClearEnv();
        Environment.SetEnvironmentVariable("DISCOURSE_URL", "https://forum.example.com");
        var o = DiscourseOptions.FromEnvironment();
        Assert.Equal("https://forum.example.com", o.Url);
        ClearEnv();
    }

    [Fact]
    public void Trailing_Slash_Is_Stripped()
    {
        ClearEnv();
        Environment.SetEnvironmentVariable("DISCOURSE_URL", "https://forum.example.com/");
        var o = DiscourseOptions.FromEnvironment();
        Assert.Equal("https://forum.example.com", o.Url);
        ClearEnv();
    }

    [Fact]
    public void No_Credentials_Means_ReadOnly()
    {
        ClearEnv();
        var o = DiscourseOptions.FromEnvironment();
        Assert.False(o.HasCredentials);
    }

    [Fact]
    public void Both_Credentials_Required_For_Write()
    {
        Assert.False(new DiscourseOptions("https://x", "user", "").HasCredentials);
        Assert.False(new DiscourseOptions("https://x", "", "pass").HasCredentials);
        Assert.True(new DiscourseOptions("https://x", "user", "pass").HasCredentials);
    }

    [Fact]
    public void Credentials_Are_Env_Driven()
    {
        ClearEnv();
        Environment.SetEnvironmentVariable("DISCOURSE_API_USERNAME", "alice");
        Environment.SetEnvironmentVariable("DISCOURSE_API_PASSWORD", "s3cret");
        var o = DiscourseOptions.FromEnvironment();
        Assert.Equal("alice", o.Username);
        Assert.Equal("s3cret", o.Password);
        Assert.True(o.HasCredentials);
        ClearEnv();
    }
}
