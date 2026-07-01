using System.Text.RegularExpressions;

namespace Sinapsi.Nats;

/// <summary>
/// Centralises how this library turns a NATS connect / publish / consume failure
/// into a caller-facing message. The contract: a message this library surfaces to
/// a caller (in a thrown exception, a log line built from it, or a returned error
/// string) must NEVER echo an NKey seed, an NKey, a PEM private-key block, or any
/// credential (password / token / bearer / api-key / authorization) that could
/// appear in an upstream exception message, a connection URL, or a seed file.
///
/// <para>
/// A NATS client exception (bad auth, TLS failure, malformed URL) can carry the
/// connection URL — which may embed <c>nats://user:password@host</c> credentials —
/// or, in a mis-wired setup, the seed string itself. The cost of leaking an NKey
/// seed (it is the private half of the identity) is catastrophic, so we fail safe:
/// any substring that looks like key material or a credential is redacted before
/// the message leaves the process, and the whole message is length-capped so a
/// pathological dump cannot blow up a log line or a response.
/// </para>
/// </summary>
internal static class NatsErrors
{
    /// <summary>Maximum length of a sanitized message handed back to a caller.</summary>
    internal const int MaxErrorLength = 2_000;

    private const string Redacted = "[redacted]";

    // A PEM private-key block (RSA/EC/PKCS#8/OpenSSH) — redact the whole block.
    private static readonly Regex PrivateKeyBlock = new(
        "-----BEGIN [^-]*PRIVATE KEY-----.*?-----END [^-]*PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // A NATS NKey seed: base32 starting with 'S' then an entity char
    // (A account, U user, O operator, N server, C cluster), 56+ base32 chars.
    // These are the private half of an nkey identity and must never surface.
    private static readonly Regex NKeySeed = new(
        @"\bS[AUONC][A-Z2-7]{54,}\b",
        RegexOptions.Compiled);

    // A NATS public NKey (U.../A.../O...): non-secret, but redacted defensively so
    // an error never doubles as an identity oracle. 56-char base32 body.
    private static readonly Regex NKeyPublic = new(
        @"\b[UAONC][A-Z2-7]{55}\b",
        RegexOptions.Compiled);

    // Credentials embedded in a connection URL: nats://user:password@host.
    private static readonly Regex UrlCredential = new(
        @"(?i)\b(nats|tls|ws|wss)://[^\s:/@]+:[^\s@]+@",
        RegexOptions.Compiled);

    // key=value / "key": "value" secrets keyed on a sensitive-looking name.
    // The value is redacted to end-of-line so multi-token credentials
    // (e.g. "Authorization: Bearer <jwt>") are fully covered, not just the first
    // token. The key name is preserved for diagnosability.
    private static readonly Regex SecretAssignment = new(
        @"(?i)\b(password|passwd|secret|token|api[-_]?key|bearer|authorization|seed|nkey)\b\s*[:=]\s*\S.*",
        RegexOptions.Compiled);

    /// <summary>
    /// Redact key material / credentials and cap the length of an arbitrary
    /// message (usually an upstream exception's <c>Message</c>) before it is
    /// surfaced to a caller or written to a log.
    /// </summary>
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "NATS operation failed with no diagnostic detail";

        var scrubbed = PrivateKeyBlock.Replace(message, Redacted);
        scrubbed = NKeySeed.Replace(scrubbed, Redacted);
        scrubbed = NKeyPublic.Replace(scrubbed, Redacted);
        scrubbed = UrlCredential.Replace(scrubbed, m =>
        {
            // Keep the scheme + host authority shape, drop the userinfo secret.
            var scheme = m.Value[..(m.Value.IndexOf("://", StringComparison.Ordinal) + 3)];
            return scheme + Redacted + "@";
        });
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

    /// <summary>
    /// Wrap an upstream exception in an <see cref="System.InvalidOperationException"/>
    /// whose message is <c>{context}: {sanitized}</c> — never echoing the raw
    /// (possibly secret-bearing) upstream message. The original exception is kept as
    /// <c>InnerException</c> for local diagnostics; only the sanitized surface message
    /// is safe to log or return.
    /// </summary>
    internal static InvalidOperationException Wrap(string context, Exception inner) =>
        new($"{context}: {Sanitize(inner.Message)}", inner);
}
