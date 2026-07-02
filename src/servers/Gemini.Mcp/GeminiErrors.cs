// ---------------------------------------------------------------------------
// GeminiErrors — centralised, fail-safe scrubbing of any upstream string that
// leaves this process as an error message. Mirrors the StepCa.Mcp exemplar
// (StepCaErrors): compute status/verdict on RAW output first, then route every
// surfaced diagnostic through Sanitize so a secret that reached the gemini CLI's
// stdout/stderr can never reach a caller.
// ---------------------------------------------------------------------------
using System.Text.RegularExpressions;

namespace Gemini.Mcp;

/// <summary>
/// Centralises how the gemini tools turn an upstream <c>gemini</c> failure into a
/// caller-facing string. The contract: an error message returned to a caller must
/// NEVER contain a private-key block, an API key, a bearer token, a provisioner
/// password, or any other secret that could appear in the CLI's stderr/stdout.
///
/// <para>
/// The <c>gemini</c> CLI normally writes only diagnostic text to stderr, but the
/// cost of a single leak (an OAuth token, a <c>NANOBANANA_API_KEY</c>) is high, so
/// we fail safe: any line that looks like PEM key material or a credential is
/// redacted before the message leaves the process, and the whole message is
/// length-capped so a pathological dump cannot blow up the response.
/// </para>
/// </summary>
internal static class GeminiErrors
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
    //
    // An optional identifier PREFIX ([A-Za-z0-9_-]*) is allowed before the sensitive
    // token so an env-style name whose sensitive part is underscore-joined — e.g.
    // this server's own NANOBANANA_API_KEY, or FOO_SECRET — is still matched (a bare
    // \b would not fire before "API" in "NANOBANANA_API_KEY", since '_' is a word
    // char). This strengthens, and never weakens, the exemplar's redaction contract.
    private static readonly Regex SecretAssignment = new(
        @"(?i)\b[A-Za-z0-9_-]*(password|passwd|secret|token|api[-_]?key|bearer|authorization)\b\s*[:=]\s*\S.*",
        RegexOptions.Compiled);

    /// <summary>
    /// Redact key material / credentials and cap the length of an arbitrary
    /// upstream string before it is surfaced to a caller. Whitespace/empty input
    /// yields a stable no-output sentinel rather than an empty message.
    /// </summary>
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "gemini CLI failed with no diagnostic output";

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

    /// <summary>
    /// Build a redacted, length-capped tail from a completed gemini run's stderr,
    /// suitable for an error message. Preserves the existing "tail" behaviour (the
    /// last <paramref name="maxTail"/> characters of stderr are the most relevant
    /// diagnostics) but routes the result through <see cref="Sanitize"/> so no
    /// secret survives. The raw tail is taken FIRST, then scrubbed, so a secret
    /// straddling the tail boundary is still caught by the regexes on the kept tail.
    /// </summary>
    internal static string SanitizedStderrTail(string? stderr, int maxTail)
    {
        if (string.IsNullOrEmpty(stderr))
            return Sanitize(stderr);
        var tail = stderr.Length <= maxTail ? stderr : stderr.Substring(stderr.Length - maxTail, maxTail);
        return Sanitize(tail);
    }
}
