using Xunit;

namespace Infisical.Mcp.Tests;

/// <summary>
/// The MCP reads its Infisical connection entirely from the environment. These tests pin
/// that mapping: NO host is baked into the binary — an unset INFISICAL_HOST_URL fails fast
/// rather than defaulting to any instance, a trailing slash is trimmed, and every override
/// is honoured.
/// </summary>
public sealed class InfisicalOptionsTests
{
    private static T WithEnv<T>(IReadOnlyDictionary<string, string?> env, Func<T> body)
    {
        var keys = new[]
        {
            "INFISICAL_HOST_URL", "INFISICAL_UNIVERSAL_AUTH_CLIENT_ID",
            "INFISICAL_UNIVERSAL_AUTH_CLIENT_SECRET", "INFISICAL_PROJECT_ID", "INFISICAL_ENV",
        };
        var saved = keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var k in keys)
                Environment.SetEnvironmentVariable(k, env.TryGetValue(k, out var v) ? v : null);
            return body();
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }
    }

    [Fact]
    public void Host_url_fails_fast_when_unset_no_host_is_baked_in()
    {
        // No host is baked into the binary. An unset INFISICAL_HOST_URL must throw, not
        // silently default to any instance (least of all a real one).
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?>(), InfisicalOptions.FromEnvironment));

        Assert.Contains("INFISICAL_HOST_URL", ex.Message);
        Assert.DoesNotContain("insalata", ex.Message);
    }

    [Fact]
    public void Host_url_fails_fast_when_blank()
    {
        // A present-but-empty value is just as unsafe as missing.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(new Dictionary<string, string?> { ["INFISICAL_HOST_URL"] = "   " },
                InfisicalOptions.FromEnvironment));

        Assert.Contains("INFISICAL_HOST_URL", ex.Message);
    }

    [Fact]
    public void Host_url_trailing_slash_is_trimmed()
    {
        var opt = WithEnv(new Dictionary<string, string?>
        {
            ["INFISICAL_HOST_URL"] = "https://secrets.example.org/",
        }, InfisicalOptions.FromEnvironment);

        Assert.Equal("https://secrets.example.org", opt.HostUrl);
    }

    [Fact]
    public void All_fields_are_read_from_the_environment()
    {
        var opt = WithEnv(new Dictionary<string, string?>
        {
            ["INFISICAL_HOST_URL"] = "https://secrets.example.org",
            ["INFISICAL_UNIVERSAL_AUTH_CLIENT_ID"] = "client-123",
            ["INFISICAL_UNIVERSAL_AUTH_CLIENT_SECRET"] = "secret-abc",
            ["INFISICAL_PROJECT_ID"] = "proj-xyz",
            ["INFISICAL_ENV"] = "staging",
        }, InfisicalOptions.FromEnvironment);

        Assert.Equal("https://secrets.example.org", opt.HostUrl);
        Assert.Equal("client-123", opt.ClientId);
        Assert.Equal("secret-abc", opt.ClientSecret);
        Assert.Equal("proj-xyz", opt.ProjectId);
        Assert.Equal("staging", opt.EnvName);
    }
}
