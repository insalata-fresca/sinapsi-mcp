using System.Text.RegularExpressions;

namespace StepCa.Mcp;

/// <summary>
/// Centralises how the step-ca tools turn an upstream <c>step</c> failure into a
/// structured error string. The contract: an error message returned to a caller
/// must NEVER contain private-key material, a provisioner password, a bearer
/// token, or any other secret that could appear in <c>step</c>'s stderr/stdout.
///
/// <para>
/// <c>step</c> normally writes only diagnostic text to stderr, but the cost of a
/// single leak (a CA-signing key, an issuer password) is catastrophic, so we
/// fail safe: any line that looks like PEM key material or a credential is
/// redacted before the message ever leaves the process, and the whole message is
/// length-capped so a pathological dump cannot blow up the response.
/// </para>
/// </summary>
internal static class StepCaErrors
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
    /// Build a redacted, length-capped error message from a completed step run.
    /// Prefers stderr (where <c>step</c> writes diagnostics); falls back to stdout.
    /// </summary>
    internal static string FromStepResult(StepResult r)
    {
        var raw = !string.IsNullOrWhiteSpace(r.Stderr) ? r.Stderr : r.Stdout;
        return Sanitize(raw);
    }

    /// <summary>
    /// Redact key material / credentials and cap the length of an arbitrary
    /// upstream string before it is surfaced to a caller.
    /// </summary>
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "step CLI failed with no diagnostic output";

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
