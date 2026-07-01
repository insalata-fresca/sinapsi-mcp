namespace Sinapsi.Forge;

/// <summary>
/// Shared, env-driven bounds for a forge <see cref="System.Net.Http.HttpClient"/>.
/// A forge call is a single outbound HTTP request; without a bound a hung upstream
/// would pin a request thread for the BCL default (100 s) or, if a caller ever set
/// an unbounded value, forever. Each host binds a canonical timeout env var through
/// <see cref="ReadHttpTimeoutMs"/>, which fails CLOSED — a non-numeric, non-positive,
/// or out-of-range value throws a clear error naming the offending variable rather
/// than silently honouring a footgun.
/// </summary>
public static class ForgeClientOptions
{
    /// <summary>Default HTTP timeout (ms) when the env var is unset.</summary>
    public const int DefaultHttpTimeoutMs = 100_000;

    /// <summary>Upper bound on a configurable HTTP timeout (ms). 10 minutes is far
    /// past any legitimate forge call; a larger value is treated as a config error,
    /// not honoured.</summary>
    public const int MaxHttpTimeoutMs = 600_000;

    /// <summary>
    /// Read + validate the HTTP timeout from <paramref name="envVar"/>. Returns the
    /// default when unset; otherwise the value must parse as an integer in
    /// <c>1..<see cref="MaxHttpTimeoutMs"/></c> ms. A value of <c>0</c> would make
    /// every request time out instantly and a negative value throws inside the
    /// HttpClient; both — and any out-of-range value — are rejected fail-closed with
    /// an <see cref="InvalidOperationException"/> naming <paramref name="envVar"/>.
    /// </summary>
    public static int ReadHttpTimeoutMs(string envVar)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultHttpTimeoutMs;

        if (!int.TryParse(raw, out var ms) || ms <= 0 || ms > MaxHttpTimeoutMs)
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected an integer in 1..{MaxHttpTimeoutMs} ms " +
                $"(default {DefaultHttpTimeoutMs}).");

        return ms;
    }
}
