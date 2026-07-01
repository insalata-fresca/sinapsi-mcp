using System.Text.RegularExpressions;

namespace Sinapsi.Mcp;

// Centralised redaction + length-capping for any error string this library
// surfaces to a caller. The library talks to an upstream MCP endpoint as a
// bearer identity, so an error path can easily quote a response body (or a
// header echo) that carries the bearer token, an NKey/seed, a signing key, or
// a PEM private-key block. The contract here: an exception message this library
// throws must NEVER echo that secret material back.
//
// The two GatewayMcpClient failure paths (a non-2xx initialize/tools-call, an
// unparseable body) both embed a slice of the upstream body in their message.
// Routing every such message through Sanitize() means a leaked token in the
// upstream body becomes [redacted] before it ever reaches the caller, and a
// pathological multi-megabyte dump is length-capped instead of ballooning the
// exception.
internal static class McpSanitizer
{
    // Maximum length of an error message this library hands back to a caller.
    internal const int MaxErrorLength = 2_000;

    private const string Redacted = "[redacted]";

    // A PEM private-key block (RSA/EC/PKCS#8/OpenSSH) -> redact the whole block.
    private static readonly Regex PrivateKeyBlock = new(
        "-----BEGIN [^-]*PRIVATE KEY-----.*?-----END [^-]*PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // A NATS NKey seed (user/account/server seeds are base32, prefixed S...).
    // A 58-char SU.../SA.../SO... token is a credential -> redact it wholesale.
    private static readonly Regex NatsSeed = new(
        @"\bS[UAONC][A-Z2-7]{50,}\b",
        RegexOptions.Compiled);

    // key=value / "key": "value" secrets keyed on a sensitive-looking name.
    // The value is redacted to end-of-line so multi-token credentials
    // (e.g. "Authorization: Bearer <jwt>") are fully covered, not just the first
    // token. The key name is preserved for diagnosability.
    private static readonly Regex SecretAssignment = new(
        @"(?i)\b(password|passwd|secret|token|api[-_]?key|bearer|authorization|signing[-_]?key|nkey|seed)\b\s*[:=]\s*\S.*",
        RegexOptions.Compiled);

    // Redact secret material and cap the length of an arbitrary upstream string
    // before it is surfaced to a caller. A null/blank input yields a neutral
    // placeholder so an empty upstream body still produces a usable message.
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "(no diagnostic detail)";

        var scrubbed = PrivateKeyBlock.Replace(message, Redacted);
        scrubbed = NatsSeed.Replace(scrubbed, Redacted);
        scrubbed = SecretAssignment.Replace(scrubbed, m =>
        {
            // Keep the key name, redact the value.
            var keyEnd = m.Value.IndexOfAny(new[] { ':', '=' });
            return keyEnd >= 0 ? m.Value[..(keyEnd + 1)] + " " + Redacted : Redacted;
        });

        scrubbed = scrubbed.Trim();
        if (scrubbed.Length > MaxErrorLength)
            scrubbed = scrubbed[..MaxErrorLength] + "... [truncated]";
        return scrubbed;
    }
}
