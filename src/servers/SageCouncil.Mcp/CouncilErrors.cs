using System.Text.RegularExpressions;

namespace SageCouncil.Mcp;

// -----------------------------------------------------------------------------
// Uniform error sanitization for the council tools.
//
// A consult fans out to external agent backends + an MCP gateway. Their error
// text (an HTTP body, a member exception message, a gateway "tool error" blob)
// is surfaced back to the caller through the consult result envelope. That text
// is untrusted: a misbehaving backend could echo a bearer token, a private key,
// or a credential assignment into an error string. Every surfaced error string
// therefore routes through Sanitize before it leaves the process — the same
// fail-safe redaction + length-cap the reference-grade exemplar (StepCaErrors)
// applies uniformly across all tools.
// -----------------------------------------------------------------------------

/// <summary>
/// Centralises how the council tools turn an upstream failure (an agent-backend
/// HTTP error, a member exception, a gateway "tool error") into a structured
/// error string. The contract: an error message returned to a caller must NEVER
/// contain a bearer token, a private key, or any other secret that could appear
/// in an upstream response.
/// </summary>
internal static class CouncilErrors
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
    /// upstream string before it is surfaced to a caller. Returns a stable
    /// placeholder for empty input so a null/blank upstream message never yields
    /// a bare empty error field.
    /// </summary>
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "council member failed with no diagnostic output";

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
