namespace OpenWrtForum.Mcp;

/// <summary>
/// Runtime config, fully env-driven; defaults are neutral so the binary carries
/// no deployment-specific wiring in source. <see cref="HasCredentials"/> gates
/// the write tools: with no username/password the server runs read-only.
/// </summary>
public sealed record DiscourseOptions(
    string Url,
    string Username,
    string Password,
    int HttpTimeoutMs)
{
    /// <summary>Default HTTP timeout (ms) when none is configured.</summary>
    internal const int DefaultHttpTimeoutMs = 30_000;

    /// <summary>Upper bound on a configurable HTTP timeout (ms). 10 minutes is far
    /// past any legitimate Discourse REST call; a larger value is treated as a
    /// config error, not honoured.</summary>
    internal const int MaxHttpTimeoutMs = 600_000;

    public bool HasCredentials => !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password);

    public static DiscourseOptions FromEnvironment() => new(
        Url: (Environment.GetEnvironmentVariable("DISCOURSE_URL") ?? "https://forum.openwrt.org")
            .TrimEnd('/'),
        Username: Environment.GetEnvironmentVariable("DISCOURSE_API_USERNAME") ?? "",
        Password: Environment.GetEnvironmentVariable("DISCOURSE_API_PASSWORD") ?? "",
        HttpTimeoutMs: ReadHttpTimeoutMs());

    /// <summary>
    /// Bounds every outbound Discourse HTTP call (applied to
    /// <see cref="System.Net.Http.HttpClient.Timeout"/>). Canonical name is
    /// <c>DISCOURSE_HTTP_TIMEOUT_MS</c>.
    /// <para>
    /// Fail-closed validation: a value of <c>0</c> would be a footgun as an
    /// <see cref="System.Net.Http.HttpClient.Timeout"/>, and a negative value
    /// throws inside the setter. Any value <c>&lt;= 0</c> or above
    /// <see cref="MaxHttpTimeoutMs"/> is rejected as invalid config — we throw a
    /// clear error naming the offending env var rather than silently honouring a
    /// footgun or crashing deep in the HTTP path.
    /// </para>
    /// </summary>
    private static int ReadHttpTimeoutMs()
    {
        const string EnvVar = "DISCOURSE_HTTP_TIMEOUT_MS";
        var raw = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrEmpty(raw))
            return DefaultHttpTimeoutMs;

        if (!int.TryParse(raw, out var ms) || ms <= 0 || ms > MaxHttpTimeoutMs)
            throw new InvalidOperationException(
                $"{EnvVar}='{raw}' is invalid: expected an integer in 1..{MaxHttpTimeoutMs} ms " +
                $"(default {DefaultHttpTimeoutMs}).");

        return ms;
    }
}
