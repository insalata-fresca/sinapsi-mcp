using System.Text.RegularExpressions;

namespace Metabase.Mcp;

// -----------------------------------------------------------------------------
// Uniform error sanitization for the Metabase MCP tools. Plain-ASCII banner so
// this file always diffs as TEXT, never binary.
// -----------------------------------------------------------------------------

/// <summary>
/// Centralises how the Metabase tools turn an upstream HTTP failure (or a low-level transport
/// exception) into a structured error string. The contract: an error message returned to a
/// caller must NEVER contain a database password, an API key, a bearer token, or any other
/// credential that could appear in a Metabase error body or in an exception message.
///
/// <para>
/// A Metabase error is usually a plain JSON <c>{message, ...}</c>, but this MCP handles database
/// connection details (<c>create_database</c> carries a DB password in its body) and runs
/// arbitrary SQL, so a pathological upstream echo could reflect a credential. The cost of a
/// single leak is high, so we fail safe: any line that looks like a credential assignment or
/// PEM private-key material is redacted before the message ever leaves the process, and the
/// whole message is length-capped so a pathological body cannot blow up the response.
/// </para>
/// </summary>
internal static class MetabaseErrors
{
    /// <summary>Maximum length of an error message handed back to a caller.</summary>
    internal const int MaxErrorLength = 2_000;

    private const string Redacted = "[redacted]";

    // A PEM private-key block (RSA/EC/PKCS#8/OpenSSH) — redact the whole block. A DB TLS client
    // key or an SSH-tunnel key could appear in an echoed connection-details body.
    private static readonly Regex PrivateKeyBlock = new(
        "-----BEGIN [^-]*PRIVATE KEY-----.*?-----END [^-]*PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // key=value / "key": "value" secrets keyed on a sensitive-looking name. The value is
    // redacted to end-of-line so multi-token credentials (e.g. "Authorization: Bearer <jwt>")
    // are fully covered, not just the first token. The key name is preserved for diagnosability.
    private static readonly Regex SecretAssignment = new(
        @"(?i)\b(password|passwd|secret|client[_-]?secret|token|api[-_]?key|apikey|bearer|authorization|x-api-key)\b""?\s*[:=]\s*""?\S.*",
        RegexOptions.Compiled);

    /// <summary>
    /// Redact key material / credentials and cap the length of an arbitrary upstream string
    /// before it is surfaced to a caller.
    /// </summary>
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Metabase request failed with no diagnostic output";

        var scrubbed = PrivateKeyBlock.Replace(message, Redacted);
        scrubbed = SecretAssignment.Replace(scrubbed, m =>
        {
            // Keep the key name (up to and including the : or =), redact the value.
            var keyEnd = m.Value.IndexOfAny(new[] { ':', '=' });
            return keyEnd >= 0 ? m.Value[..(keyEnd + 1)] + " " + Redacted : Redacted;
        });

        scrubbed = scrubbed.Trim();
        if (scrubbed.Length > MaxErrorLength)
            scrubbed = scrubbed[..MaxErrorLength] + "… [truncated]";
        return scrubbed;
    }
}
