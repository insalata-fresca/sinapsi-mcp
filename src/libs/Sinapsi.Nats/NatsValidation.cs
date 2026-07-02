namespace Sinapsi.Nats;

/// <summary>
/// Input + configuration validation for the public seams of this library. Every
/// public-API string that flows into a NATS operation (a connection URL, a publish
/// subject, a CloudEvents <c>source</c>, an NKey) is checked here BEFORE it reaches
/// the client, so a malformed value is rejected with a clear, structured reason
/// instead of failing opaquely deep inside NATS.Net or — worse — silently
/// connecting somewhere unintended.
///
/// <para>
/// The validators return <c>null</c> when the value is valid, otherwise a short
/// human-readable reason. Callers turn a non-null reason into a thrown
/// <see cref="System.ArgumentException"/> / <see cref="System.InvalidOperationException"/>
/// that names the offending field or env var (fail-closed).
/// </para>
/// </summary>
internal static class NatsValidation
{
    /// <summary>Upper bound on a NATS server URL. A URL longer than this is
    /// certainly malformed; the cap refuses an unbounded blob before it reaches
    /// the client.</summary>
    internal const int MaxUrlLength = 2_048;

    /// <summary>Upper bound on a publish subject. NATS itself limits a subject
    /// token tree; 512 is a generous ceiling that still refuses an abusive value.</summary>
    internal const int MaxSubjectLength = 512;

    /// <summary>Upper bound on a CloudEvents <c>source</c> URI.</summary>
    internal const int MaxSourceLength = 512;

    /// <summary>Upper bound on an NKey / seed string.</summary>
    internal const int MaxNKeyLength = 256;

    /// <summary>Schemes NATS.Net accepts for a server URL.</summary>
    private static readonly string[] AllowedSchemes = { "nats://", "tls://", "ws://", "wss://" };

    /// <summary>
    /// Validate a NATS server URL. Must be present, within length, control-char
    /// free, and carry a scheme NATS.Net understands. Returns <c>null</c> when
    /// valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "NATS URL is required (set NATS_URL)";
        if (url.Length > MaxUrlLength)
            return $"NATS URL too long ({url.Length} chars; max {MaxUrlLength})";
        if (ContainsControlOrWhitespace(url))
            return "NATS URL contains control or whitespace characters";

        var ok = false;
        foreach (var s in AllowedSchemes)
            if (url.StartsWith(s, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
        if (!ok)
            return "NATS URL must start with nats:// tls:// ws:// or wss://";

        return null;
    }

    /// <summary>
    /// Validate a publish subject. Must be present, within length, and free of
    /// control chars, whitespace, and NUL. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return "subject is required";
        if (subject.Length > MaxSubjectLength)
            return $"subject too long ({subject.Length} chars; max {MaxSubjectLength})";
        if (ContainsControlOrWhitespace(subject))
            return "subject contains control or whitespace characters";
        // A leading/trailing '.' or an empty token yields an unroutable subject.
        if (subject[0] == '.' || subject[^1] == '.' || subject.Contains(".."))
            return "subject has an empty token (leading/trailing/double '.')";
        return null;
    }

    /// <summary>
    /// Validate the CloudEvents <c>source</c> producer URI. Required and
    /// control-char free; length-capped. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return "source is required";
        if (source.Length > MaxSourceLength)
            return $"source too long ({source.Length} chars; max {MaxSourceLength})";
        if (ContainsControlOrNewline(source))
            return "source contains control characters";
        return null;
    }

    /// <summary>
    /// Validate a public NKey when one is configured. NATS public NKeys are
    /// base32; we reject control chars, whitespace, and an over-long blob. A
    /// <c>null</c>/empty value is allowed (nkey auth is opt-in) — this only rejects
    /// a malformed non-empty value. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateNKeyPublic(string? nkey)
    {
        if (string.IsNullOrEmpty(nkey))
            return null;
        if (nkey.Length > MaxNKeyLength)
            return $"NATS_NKEY too long ({nkey.Length} chars; max {MaxNKeyLength})";
        if (ContainsControlOrWhitespace(nkey))
            return "NATS_NKEY contains control or whitespace characters";
        return null;
    }

    private static bool ContainsControlOrNewline(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c)) return true;
        return false;
    }

    private static bool ContainsControlOrWhitespace(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c) || char.IsWhiteSpace(c)) return true;
        return false;
    }
}
