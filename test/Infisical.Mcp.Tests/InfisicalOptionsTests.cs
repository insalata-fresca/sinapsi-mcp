using Xunit;

namespace Infisical.Mcp.Tests;

/// <summary>
/// The MCP reads its Infisical connection entirely from the environment. These tests pin
/// that mapping: the host URL default is a neutral example (no real instance baked in),
/// a trailing slash is trimmed, and every override is honoured.
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
    public void Host_url_defaults_to_a_neutral_example_when_unset()
    {
        var opt = WithEnv(new Dictionary<string, string?>(), InfisicalOptions.FromEnvironment);

        // The default must be a neutral placeholder, never a real instance.
        Assert.Equal("https://infisical.example.com", opt.HostUrl);
        Assert.DoesNotContain("insalata", opt.HostUrl);
        Assert.Equal("dev", opt.EnvName);
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
