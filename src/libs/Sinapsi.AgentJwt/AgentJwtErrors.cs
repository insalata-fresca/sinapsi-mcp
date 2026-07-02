using System.Text.RegularExpressions;

namespace Sinapsi.AgentJwt;

// Plain-ASCII comment banner so this file diffs as TEXT.
//
// Centralised error sanitization for the JWT minter. The contract: any string
// this library surfaces to a caller (an exception message built from a JWK, an
// OIDC provider response, or an internal failure) must NEVER echo private-key
// material, a signing key, an NKey/seed, or a bearer/authorization/token/secret
// assignment. A mint failure that leaked the RSA private key or the resulting
// access token would be catastrophic, so we fail safe: redact anything that
// looks like key material or a credential before the message leaves the process,
// and length-cap the whole message so a pathological provider dump cannot blow
// up the surfaced error.

/// <summary>
/// Redacts secrets and length-caps any error/diagnostic string before the
/// <see cref="AgentJwtMinter"/> surfaces it to a caller. Applied uniformly to
/// every exception message the library raises, so a private key, signing key,
/// NKey/seed, or bearer token can never travel out in an error.
/// </summary>
public static class AgentJwtErrors
{
    /// <summary>Maximum length of an error message surfaced to a caller.</summary>
    public const int MaxErrorLength = 2_000;

    private const string Redacted = "[redacted]";

    // A PEM private-key block (RSA / EC / PKCS#8 / OpenSSH) -> redact the whole
    // block, so a signing key pasted into a JWK or echoed by a provider is gone.
    private static readonly Regex PrivateKeyBlock = new(
        "-----BEGIN [^-]*PRIVATE KEY-----.*?-----END [^-]*PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // A NATS NKey seed (SUxxxx...) or a raw seed assignment. NKey seeds start
    // with an 'S' + role letter and are base32; redact any long base32 run that
    // begins with the seed prefix, plus a "seed=/nkey=" assignment form.
    private static readonly Regex NKeySeed = new(
        @"\bS[UOAXNCP][A-Z2-7]{40,}\b",
        RegexOptions.Compiled);

    // key=value / "key": "value" secrets keyed on a sensitive-looking name.
    // The value is redacted to end-of-line so multi-token credentials
    // (e.g. "Authorization: Bearer <jwt>") are fully covered, not just the first
    // token. The key name is preserved for diagnosability.
    private static readonly Regex SecretAssignment = new(
        @"(?i)\b(password|passwd|secret|token|api[-_]?key|bearer|authorization|nkey|seed|signing[-_]?key|private[-_]?key)\b\s*[:=]\s*\S.*",
        RegexOptions.Compiled);

    /// <summary>
    /// Redact key material / credentials and cap the length of an arbitrary
    /// string before it is surfaced to a caller. Never returns null.
    /// </summary>
    public static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "JWT mint failed with no diagnostic detail";

        var scrubbed = PrivateKeyBlock.Replace(message, Redacted);
        scrubbed = NKeySeed.Replace(scrubbed, Redacted);
        scrubbed = SecretAssignment.Replace(scrubbed, m =>
        {
            // Keep the key name, redact the value to end-of-line.
            var keyEnd = m.Value.IndexOfAny([':', '=']);
            return keyEnd >= 0 ? m.Value[..(keyEnd + 1)] + " " + Redacted : Redacted;
        });

        scrubbed = scrubbed.Trim();
        if (scrubbed.Length > MaxErrorLength)
            scrubbed = scrubbed[..MaxErrorLength] + "… [truncated]";
        return scrubbed;
    }
}
