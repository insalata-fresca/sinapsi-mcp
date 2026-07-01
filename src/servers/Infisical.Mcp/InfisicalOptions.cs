namespace Infisical.Mcp;

/// <summary>
/// Infisical connection + Universal-Auth machine-identity config, read from the
/// environment. The clientId/secret are the MCP's OWN machine identity; inject them
/// at deploy (e.g. via an env file) and never bake them into the image.
///
/// <para>
/// Fail-closed: every value the server cannot function without is <b>required</b> and
/// throws (naming the offending env var) if missing at startup, rather than binding an
/// empty string and failing opaquely on the first login. The one bounded numeric — the
/// HTTP timeout — has a sane default AND a hard ceiling; a non-numeric / out-of-range
/// value is rejected as a config error rather than silently honoured.
/// </para>
/// </summary>
public sealed record InfisicalOptions
{
    public required string HostUrl { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string ProjectId { get; init; }
    public string EnvName { get; init; } = "dev";

    /// <summary>Bounds every Infisical REST call (login + folder + secret ops).</summary>
    public int HttpTimeoutMs { get; init; } = DefaultHttpTimeoutMs;

    /// <summary>Default HTTP timeout (ms) when none is configured.</summary>
    internal const int DefaultHttpTimeoutMs = 30_000;

    /// <summary>Upper bound on a configurable HTTP timeout (ms). 10 minutes is far past
    /// any legitimate Infisical call; a larger value is treated as a config error, not
    /// honoured.</summary>
    internal const int MaxHttpTimeoutMs = 600_000;

    public static InfisicalOptions FromEnvironment() => new()
    {
        // No host is baked into the binary. INFISICAL_HOST_URL is required and is
        // supplied at deploy (e.g. /etc/infisical-mcp/infisical.env); if it is missing
        // we fail fast rather than silently default to ANY instance.
        HostUrl = RequiredHostUrl().TrimEnd('/'),
        // The MCP's own machine identity + the target project are all required. Binding
        // an empty string here would only defer the failure to the first login, where it
        // would surface as an opaque 401/404 instead of a clear startup error.
        ClientId = EnvRequired("INFISICAL_UNIVERSAL_AUTH_CLIENT_ID"),
        ClientSecret = EnvRequired("INFISICAL_UNIVERSAL_AUTH_CLIENT_SECRET"),
        ProjectId = EnvRequired("INFISICAL_PROJECT_ID"),
        EnvName = Env("INFISICAL_ENV", "dev"),
        HttpTimeoutMs = ReadHttpTimeoutMs(),
    };

    private static string Env(string k, string def) =>
        System.Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : def;

    private static string EnvRequired(string k) =>
        System.Environment.GetEnvironmentVariable(k) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException(
                $"{k} is required (inject it via the deploy env, e.g. /etc/infisical-mcp/infisical.env)");

    private static string RequiredHostUrl()
    {
        var url = System.Environment.GetEnvironmentVariable("INFISICAL_HOST_URL");
        return string.IsNullOrWhiteSpace(url)
            ? throw new InvalidOperationException(
                "INFISICAL_HOST_URL is required (no host is baked into the image); " +
                "supply it via the deploy env (e.g. /etc/infisical-mcp/infisical.env)")
            : url;
    }

    /// <summary>
    /// Read + fail-closed-validate the HTTP timeout. Canonical name is
    /// <c>INFISICAL_HTTP_TIMEOUT_MS</c>. A value of <c>0</c> would make every request time
    /// out instantly and a negative value throws inside the <see cref="HttpClient"/> ctor;
    /// any value <c>&lt;= 0</c> or above <see cref="MaxHttpTimeoutMs"/> is rejected as
    /// invalid config — we throw a clear error naming the offending env var rather than
    /// silently honouring a footgun.
    /// </summary>
    private static int ReadHttpTimeoutMs()
    {
        var raw = System.Environment.GetEnvironmentVariable("INFISICAL_HTTP_TIMEOUT_MS");
        if (string.IsNullOrEmpty(raw))
            return DefaultHttpTimeoutMs;

        if (!int.TryParse(raw, out var ms) || ms <= 0 || ms > MaxHttpTimeoutMs)
            throw new InvalidOperationException(
                $"INFISICAL_HTTP_TIMEOUT_MS='{raw}' is invalid: expected an integer in " +
                $"1..{MaxHttpTimeoutMs} ms (default {DefaultHttpTimeoutMs}).");

        return ms;
    }
}
