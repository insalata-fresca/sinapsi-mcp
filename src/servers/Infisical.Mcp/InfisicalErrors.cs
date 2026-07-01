using System.Text.RegularExpressions;

namespace Infisical.Mcp;

/// <summary>
/// Centralises how the Infisical tools turn an upstream failure into a caller-facing
/// error string. The contract: an error message returned to a caller must NEVER contain
/// a secret value, a bearer token, the Universal-Auth client secret, or any PEM key
/// material that could appear in an Infisical response body / exception message.
///
/// <para>
/// This server's whole reason for existing is transcript-safety, so a leak in an ERROR
/// path — the one place a raw upstream body can surface — would defeat the point. We fail
/// safe: any line that looks like PEM key material or a credential assignment is redacted
/// before the message ever leaves the process, and the whole message is length-capped so
/// a pathological body cannot blow up the response.
/// </para>
/// </summary>
internal static class InfisicalErrors
{
    /// <summary>Maximum length of an error message handed back to a caller.</summary>
    internal const int MaxErrorLength = 2_000;

    private const string Redacted = "[redacted]";

    // A PEM private-key block (RSA/EC/PKCS#8/OpenSSH) — redact the whole block.
    private static readonly Regex PrivateKeyBlock = new(
        "-----BEGIN [^-]*PRIVATE KEY-----.*?-----END [^-]*PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // key=value / "key": "value" secrets keyed on a sensitive-looking name. The value is
    // redacted to end-of-line so multi-token credentials (e.g. "Authorization: Bearer
    // <jwt>") are fully covered, not just the first token. The key name is preserved for
    // diagnosability.
    // A closing quote / whitespace may sit between the key and the ':' / '=' separator
    // (e.g. a JSON body: "secretValue": "topsecret") — allow it so both shell-style
    // (password=…) and JSON-style ("token":"…") credentials are covered.
    private static readonly Regex SecretAssignment = new(
        @"(?i)\b(password|passwd|secret|secretValue|token|accessToken|api[-_]?key|bearer|authorization)\b""?\s*[:=]\s*""?\S.*",
        RegexOptions.Compiled);

    /// <summary>
    /// Redact key material / credentials and cap the length of an arbitrary upstream string
    /// before it is surfaced to a caller. Returns a generic sentinel for null/blank input.
    /// </summary>
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "infisical request failed with no diagnostic output";

        var scrubbed = PrivateKeyBlock.Replace(message, Redacted);
        scrubbed = SecretAssignment.Replace(scrubbed, m =>
        {
            // Keep the key name (and its ':' / '='), redact the value.
            var keyEnd = m.Value.IndexOfAny(new[] { ':', '=' });
            return keyEnd >= 0 ? m.Value[..(keyEnd + 1)] + " " + Redacted : Redacted;
        });

        scrubbed = scrubbed.Trim();
        if (scrubbed.Length > MaxErrorLength)
            scrubbed = scrubbed[..MaxErrorLength] + "… [truncated]";
        return scrubbed;
    }
}
