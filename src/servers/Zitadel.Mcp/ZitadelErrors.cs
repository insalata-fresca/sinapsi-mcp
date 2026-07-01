using System.Text.RegularExpressions;

namespace Zitadel.Mcp;

/// <summary>
/// Centralises how the ZITADEL tools turn an upstream HTTP failure into a structured error
/// string. The contract: an error message returned to a caller must NEVER contain a bearer
/// token, a client secret, a private-key block, or any other credential that could appear in
/// ZITADEL's error body (or in a low-level transport exception message).
///
/// <para>
/// ZITADEL normally returns only a JSON <c>{code, message, …}</c> on error, but this MCP mints
/// secrets (PATs, OIDC client secrets, machine-key JSON), and the cost of a single leak is
/// catastrophic, so we fail safe: any line that looks like a credential assignment or PEM key
/// material is redacted before the message ever leaves the process, and the whole message is
/// length-capped so a pathological body cannot blow up the response.
/// </para>
/// </summary>
internal static class ZitadelErrors
{
    /// <summary>Maximum length of an error message handed back to a caller.</summary>
    internal const int MaxErrorLength = 2_000;

    private const string Redacted = "[redacted]";

    // A PEM private-key block (RSA/EC/PKCS#8/OpenSSH) — redact the whole block. A ZITADEL
    // machine key is JSON-wrapped PEM; a malformed-response echo could carry one.
    private static readonly Regex PrivateKeyBlock = new(
        "-----BEGIN [^-]*PRIVATE KEY-----.*?-----END [^-]*PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // key=value / "key": "value" secrets keyed on a sensitive-looking name. The value is
    // redacted to end-of-line so multi-token credentials (e.g. "Authorization: Bearer <jwt>")
    // are fully covered, not just the first token. The key name is preserved for diagnosability.
    private static readonly Regex SecretAssignment = new(
        @"(?i)\b(password|passwd|secret|client[_-]?secret|token|api[-_]?key|bearer|authorization)\b""?\s*[:=]\s*""?\S.*",
        RegexOptions.Compiled);

    /// <summary>
    /// Redact key material / credentials and cap the length of an arbitrary upstream string
    /// before it is surfaced to a caller.
    /// </summary>
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "ZITADEL request failed with no diagnostic output";

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
