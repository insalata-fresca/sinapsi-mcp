using System.Text.RegularExpressions;

namespace OpenWrtForum.Mcp;

/// <summary>
/// Centralises how the forum tools turn an upstream Discourse failure into a
/// caller-facing error string. The contract: an error message returned to a
/// caller must NEVER contain a private-key block, an account password, a session
/// token, an API key, or any other secret that could appear in the forum's error
/// body or in an exception message that echoes a request.
///
/// <para>
/// Discourse normally returns only diagnostic JSON, but the cost of a single
/// leak (the configured account password, a session cookie/token echoed back in a
/// verbose error) is high enough that we fail safe: any span that looks like PEM
/// key material or a credential assignment is redacted before the message ever
/// leaves the process, and the whole message is length-capped so a pathological
/// upstream body cannot blow up the response.
/// </para>
/// </summary>
internal static class OpenWrtForumErrors
{
    /// <summary>Maximum length of an error message handed back to a caller.</summary>
    internal const int MaxErrorLength = 2_000;

    private const string Redacted = "[redacted]";

    // A PEM private-key block (RSA/EC/PKCS#8/OpenSSH) — redact the whole block.
    private static readonly Regex PrivateKeyBlock = new(
        "-----BEGIN [^-]*PRIVATE KEY-----.*?-----END [^-]*PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // key=value / "key": "value" secrets keyed on a sensitive-looking name.
    // The value is redacted to end-of-line so multi-token credentials
    // (e.g. "Authorization: Bearer <jwt>") are fully covered, not just the first
    // token. The key name is preserved for diagnosability.
    private static readonly Regex SecretAssignment = new(
        @"(?i)\b(password|passwd|secret|token|api[-_]?key|bearer|authorization)\b\s*[:=]\s*\S.*",
        RegexOptions.Compiled);

    /// <summary>
    /// Redact key material / credentials and cap the length of an arbitrary
    /// upstream string before it is surfaced to a caller. This is the single
    /// choke point every tool routes its error text through — reads included.
    /// </summary>
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "forum request failed with no diagnostic output";

        var scrubbed = PrivateKeyBlock.Replace(message, Redacted);
        scrubbed = SecretAssignment.Replace(scrubbed, m =>
        {
            // Keep the key name, redact the value.
            var keyEnd = m.Value.IndexOfAny(new[] { ':', '=' });
            return keyEnd >= 0 ? m.Value[..(keyEnd + 1)] + " " + Redacted : Redacted;
        });

        scrubbed = scrubbed.Trim();
        if (scrubbed.Length > MaxErrorLength)
            scrubbed = scrubbed[..MaxErrorLength] + "… [truncated]";
        return scrubbed;
    }
}
