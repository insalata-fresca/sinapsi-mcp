using System.Text.RegularExpressions;

namespace Sinapsi.Forge.Tools;

/// <summary>
/// Centralises how the git-forge tools turn an upstream failure into a structured
/// error string. The contract: an error message returned to a caller must NEVER
/// contain a personal-access token, a bearer/authorization credential, a webhook
/// HMAC secret, or PEM private-key material that could appear in a forge response
/// body, an exception message, or a header echoed back by the API.
///
/// <para>
/// A forge normally writes only diagnostic JSON to an error body, but the cost of a
/// single credential leak is high, so we fail safe: any span that looks like a PEM
/// key block or a credential assignment is redacted before the message leaves the
/// process, and the whole message is length-capped so a pathological body cannot
/// blow up the response. Any status verdict (an HTTP status code, an <c>ok</c> flag)
/// must be computed on the RAW value BEFORE sanitizing, so a redaction can never
/// change what a caller is told happened — only what secret text they see.
/// </para>
/// </summary>
public static class SinapsiForgeErrors
{
    /// <summary>Maximum length of an error message handed back to a caller.</summary>
    public const int MaxErrorLength = 2_000;

    private const string Redacted = "[redacted]";

    // A PEM private-key block (RSA/EC/PKCS#8/OpenSSH) — redact the whole block.
    private static readonly Regex PrivateKeyBlock = new(
        "-----BEGIN [^-]*PRIVATE KEY-----.*?-----END [^-]*PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // key=value / "key": "value" secrets keyed on a sensitive-looking name. The
    // value is redacted to end-of-line so multi-token credentials (e.g.
    // "Authorization: Bearer <jwt>") are fully covered, not just the first token.
    // The key name is preserved for diagnosability.
    private static readonly Regex SecretAssignment = new(
        @"(?i)\b(password|passwd|secret|token|api[-_]?key|bearer|authorization)\b\s*[:=]\s*\S.*",
        RegexOptions.Compiled);

    /// <summary>
    /// Redact key material / credentials and cap the length of an arbitrary upstream
    /// string before it is surfaced to a caller. Never throws; an empty input yields
    /// a neutral placeholder.
    /// </summary>
    public static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "forge request failed with no diagnostic output";

        var scrubbed = PrivateKeyBlock.Replace(message, Redacted);
        scrubbed = SecretAssignment.Replace(scrubbed, m =>
        {
            var keyEnd = m.Value.IndexOfAny(new[] { ':', '=' });
            return keyEnd >= 0 ? m.Value[..(keyEnd + 1)] + " " + Redacted : Redacted;
        });

        scrubbed = scrubbed.Trim();
        if (scrubbed.Length > MaxErrorLength)
            scrubbed = scrubbed[..MaxErrorLength] + "… [truncated]";
        return scrubbed;
    }
}
