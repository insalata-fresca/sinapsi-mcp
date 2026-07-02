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
            "INFISICAL_HTTP_TIMEOUT_MS",
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

    /// <summary>A full, valid environment — the base the fail-fast tests remove one key
    /// from at a time to prove each required var is enforced.</summary>
    private static Dictionary<string, string?> FullEnv() => new()
    {
        ["INFISICAL_HOST_URL"] = "https://secrets.example.org",
        ["INFISICAL_UNIVERSAL_AUTH_CLIENT_ID"] = "client-123",
        ["INFISICAL_UNIVERSAL_AUTH_CLIENT_SECRET"] = "secret-abc",
        ["INFISICAL_PROJECT_ID"] = "proj-xyz",
        ["INFISICAL_ENV"] = "staging",
    };

    [Fact]
    public void Host_url_trailing_slash_is_trimmed()
    {
        var env = FullEnv();
        env["INFISICAL_HOST_URL"] = "https://secrets.example.org/";

        var opt = WithEnv(env, InfisicalOptions.FromEnvironment);

        Assert.Equal("https://secrets.example.org", opt.HostUrl);
    }

    [Fact]
    public void All_fields_are_read_from_the_environment()
    {
        var opt = WithEnv(FullEnv(), InfisicalOptions.FromEnvironment);

        Assert.Equal("https://secrets.example.org", opt.HostUrl);
        Assert.Equal("client-123", opt.ClientId);
        Assert.Equal("secret-abc", opt.ClientSecret);
        Assert.Equal("proj-xyz", opt.ProjectId);
        Assert.Equal("staging", opt.EnvName);
    }

    [Fact]
    public void Env_defaults_to_dev_when_unset()
    {
        var env = FullEnv();
        env.Remove("INFISICAL_ENV");

        var opt = WithEnv(env, InfisicalOptions.FromEnvironment);

        Assert.Equal("dev", opt.EnvName);
    }

    // ── the newly-required machine-identity + project vars fail closed ────────────────
    // Binding an empty string would only defer the failure to the first login, where it
    // would surface as an opaque 401/404 instead of a clear, named startup error.
    [Theory]
    [InlineData("INFISICAL_UNIVERSAL_AUTH_CLIENT_ID")]
    [InlineData("INFISICAL_UNIVERSAL_AUTH_CLIENT_SECRET")]
    [InlineData("INFISICAL_PROJECT_ID")]
    public void Required_var_fails_fast_when_unset_naming_the_var(string missing)
    {
        var env = FullEnv();
        env.Remove(missing);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(env, InfisicalOptions.FromEnvironment));

        Assert.Contains(missing, ex.Message);
    }

    // ── HTTP timeout: default, override, and fail-closed clamp ────────────────────────
    [Fact]
    public void Http_timeout_defaults_when_unset()
    {
        var opt = WithEnv(FullEnv(), InfisicalOptions.FromEnvironment);
        Assert.Equal(InfisicalOptions.DefaultHttpTimeoutMs, opt.HttpTimeoutMs);
    }

    [Fact]
    public void Http_timeout_is_read_from_the_environment()
    {
        var env = FullEnv();
        env["INFISICAL_HTTP_TIMEOUT_MS"] = "12345";

        var opt = WithEnv(env, InfisicalOptions.FromEnvironment);

        Assert.Equal(12345, opt.HttpTimeoutMs);
    }

    // 0 would make every request time out instantly; a negative value throws inside the
    // HttpClient ctor. Both are rejected as invalid config with a clear error naming the
    // offending env var, rather than being silently honoured.
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-30000")]
    [InlineData("notanumber")]
    public void Http_timeout_bad_value_is_rejected_naming_the_var(string bad)
    {
        var env = FullEnv();
        env["INFISICAL_HTTP_TIMEOUT_MS"] = bad;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(env, InfisicalOptions.FromEnvironment));

        Assert.Contains("INFISICAL_HTTP_TIMEOUT_MS", ex.Message);
    }

    [Fact]
    public void Http_timeout_above_ceiling_is_rejected()
    {
        var env = FullEnv();
        env["INFISICAL_HTTP_TIMEOUT_MS"] =
            (InfisicalOptions.MaxHttpTimeoutMs + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WithEnv(env, InfisicalOptions.FromEnvironment));

        Assert.Contains("INFISICAL_HTTP_TIMEOUT_MS", ex.Message);
    }
}
