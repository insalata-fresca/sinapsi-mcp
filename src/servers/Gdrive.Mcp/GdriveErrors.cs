using System.Text.RegularExpressions;

namespace Gdrive.Mcp;

// ---------------------------------------------------------------------------
// Centralises how the Drive tools turn an upstream failure into a structured
// error string. The contract: an error message returned to a caller must NEVER
// contain an OAuth token, a refresh token, a bearer/authorization header, or a
// client secret that could appear in a Google API exception, an HTTP diagnostic,
// or a wrapped TokenResponseException.
//
// The Drive client normally surfaces only sanitised diagnostics, but the cost of
// a single leak (a refresh token grants long-lived access to the whole Drive) is
// high, so we fail safe: any assignment that looks like a credential is redacted
// before the message ever leaves the process, and the whole message is length-
// capped so a pathological dump cannot blow up the response.
//
// Every surfaced upstream/error string in EVERY tool routes through Sanitize.
// ---------------------------------------------------------------------------

/// <summary>
/// Static, throw-free error sanitiser for the Drive tools. Mirrors the
/// StepCa.Mcp <c>StepCaErrors</c> exemplar: <see cref="Sanitize"/> redacts
/// credential-shaped material and length-caps the result.
/// </summary>
internal static class GdriveErrors
{
    /// <summary>Maximum length of an error message handed back to a caller.</summary>
    internal const int MaxErrorLength = 2_000;

    private const string Redacted = "[redacted]";

    // A PEM private-key block (a client-secret file or a stray key can be echoed
    // in a diagnostic) — redact the whole block.
    private static readonly Regex PrivateKeyBlock = new(
        "-----BEGIN [^-]*PRIVATE KEY-----.*?-----END [^-]*PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // key=value / "key": "value" secrets keyed on a sensitive-looking name.
    // The value is redacted to end-of-line so multi-token credentials
    // (e.g. "Authorization: Bearer <jwt>", "refresh_token=1//0g…") are fully
    // covered, not just the first token. The key name is preserved for
    // diagnosability. Covers the OAuth-specific token names on top of the
    // generic credential vocabulary.
    private static readonly Regex SecretAssignment = new(
        @"(?i)\b(password|passwd|secret|token|refresh[-_]?token|access[-_]?token|client[-_]?secret|api[-_]?key|bearer|authorization)\b\s*[:=]\s*\S.*",
        RegexOptions.Compiled);

    /// <summary>
    /// Redact key material / credentials and cap the length of an arbitrary
    /// upstream string before it is surfaced to a caller. Returns a generic
    /// sentinel for empty input so an error field is never null-shaped.
    /// </summary>
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Drive request failed with no diagnostic output";

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
    /// Convenience wrapper: sanitise the message of a thrown upstream exception
    /// before it is surfaced. Mirrors the "route every surfaced string through
    /// Sanitize" contract for the exception path.
    /// </summary>
    internal static string FromException(Exception e) => Sanitize(e.Message);
}
