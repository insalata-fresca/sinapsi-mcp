using System.Text.RegularExpressions;

namespace ConfigSpine.Mcp;

/// <summary>
/// Redacts anything secret-shaped from an error message before it can reach the caller. The NATS
/// publisher library already wraps connect/publish failures through its own sanitizer, but this is
/// a defence-in-depth backstop at the tool boundary: a NATS nkey <c>seed</c> (Base32, <c>S</c>
/// prefixed) or a connection URL (which could embed credentials) must never transit a tool result.
/// </summary>
internal static partial class ConfigEventErrors
{
    // NATS nkey seeds are Base32, 'S'-prefixed, and long (a user seed is ~58 chars). Redact any
    // long S-prefixed Base32 run so a mis-wired seed can never surface in an error.
    [GeneratedRegex(@"S[A-Z2-7]{40,}", RegexOptions.CultureInvariant)]
    private static partial Regex SeedLike();

    // A NATS/TLS/ws connection URL can carry embedded credentials — redact the whole token.
    [GeneratedRegex(@"(?:nats|tls|ws|wss)://\S+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlLike();

    /// <summary>Return <paramref name="message"/> with any seed-like or URL-like substring
    /// replaced by <c>[redacted]</c>. Never throws; a null/empty message becomes a generic reason.</summary>
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "publish failed";
        var redacted = SeedLike().Replace(message, "[redacted]");
        redacted = UrlLike().Replace(redacted, "[redacted]");
        return redacted;
    }
}
