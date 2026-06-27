using Xunit;

namespace Gdrive.Mcp.Tests;

/// <summary>
/// <see cref="GdriveConfig.FromEnvironment"/> must bake no deployment topology: with no env
/// vars set the download URL is a neutral local placeholder, and every value is overridable.
/// These tests mutate process env vars, so they run in one non-parallel collection.
/// </summary>
[Collection("env")]
public sealed class GdriveConfigTests
{
    private static readonly string[] Vars =
    {
        "GDRIVE_MCP_CRED_DIR", "GDRIVE_MCP_OAUTH_CLIENT", "GDRIVE_MCP_TOKEN",
        "GDRIVE_MCP_APP_NAME", "GDRIVE_MCP_DOWNLOAD_BASE_HOST", "GDRIVE_MCP_DOWNLOAD_BASE_URL",
        "GDRIVE_MCP_DOWNLOAD_TTL_SECONDS", "GDRIVE_MCP_PORT",
    };

    private static void ClearAll()
    {
        foreach (var v in Vars) Environment.SetEnvironmentVariable(v, null);
    }

    [Fact]
    public void Defaults_AreNeutral_NoBakedTopology()
    {
        ClearAll();
        try
        {
            var cfg = GdriveConfig.FromEnvironment();

            // No private IP or hostname baked in — neutral loopback default.
            Assert.StartsWith("http://127.0.0.1:", cfg.DownloadBaseUrl);
            Assert.EndsWith(":9217", cfg.DownloadBaseUrl);
            Assert.DoesNotContain("10.42.0.121", cfg.DownloadBaseUrl);
            Assert.Equal("gdrive-mcp", cfg.ApplicationName);
            Assert.Equal(600, cfg.DownloadTtlSeconds);
        }
        finally { ClearAll(); }
    }

    [Fact]
    public void BaseHost_OverridesTheUrlHost()
    {
        ClearAll();
        try
        {
            Environment.SetEnvironmentVariable("GDRIVE_MCP_DOWNLOAD_BASE_HOST", "files.example.com");
            Environment.SetEnvironmentVariable("GDRIVE_MCP_PORT", "8080");
            var cfg = GdriveConfig.FromEnvironment();
            Assert.Equal("http://files.example.com:8080", cfg.DownloadBaseUrl);
        }
        finally { ClearAll(); }
    }

    [Fact]
    public void ExplicitBaseUrl_Wins()
    {
        ClearAll();
        try
        {
            Environment.SetEnvironmentVariable("GDRIVE_MCP_DOWNLOAD_BASE_URL", "https://dl.example:9000");
            Environment.SetEnvironmentVariable("GDRIVE_MCP_DOWNLOAD_BASE_HOST", "ignored");
            var cfg = GdriveConfig.FromEnvironment();
            Assert.Equal("https://dl.example:9000", cfg.DownloadBaseUrl);
        }
        finally { ClearAll(); }
    }

    [Fact]
    public void CredPaths_DeriveFromCredDir_AndAreOverridable()
    {
        ClearAll();
        try
        {
            Environment.SetEnvironmentVariable("GDRIVE_MCP_CRED_DIR", "/srv/creds");
            var cfg = GdriveConfig.FromEnvironment();
            Assert.Equal(Path.Combine("/srv/creds", "gcp-oauth.keys.json"), cfg.OAuthClientPath);
            Assert.Equal(Path.Combine("/srv/creds", "token.json"), cfg.RefreshTokenPath);

            Environment.SetEnvironmentVariable("GDRIVE_MCP_OAUTH_CLIENT", "/x/client.json");
            Environment.SetEnvironmentVariable("GDRIVE_MCP_TOKEN", "/x/tok.json");
            cfg = GdriveConfig.FromEnvironment();
            Assert.Equal("/x/client.json", cfg.OAuthClientPath);
            Assert.Equal("/x/tok.json", cfg.RefreshTokenPath);
        }
        finally { ClearAll(); }
    }

    [Theory]
    [InlineData("0", 600)]      // non-positive falls back to default
    [InlineData("-5", 600)]
    [InlineData("not-a-number", 600)]
    [InlineData("30", 30)]
    public void Ttl_ParsesOrFallsBack(string raw, int expected)
    {
        ClearAll();
        try
        {
            Environment.SetEnvironmentVariable("GDRIVE_MCP_DOWNLOAD_TTL_SECONDS", raw);
            Assert.Equal(expected, GdriveConfig.FromEnvironment().DownloadTtlSeconds);
        }
        finally { ClearAll(); }
    }
}

[CollectionDefinition("env", DisableParallelization = true)]
public sealed class EnvCollection { }
