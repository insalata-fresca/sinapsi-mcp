using System.Text.RegularExpressions;

namespace Sshgw.Mcp;

/// <summary>
/// Centralises how the sshgw tools turn upstream text (a remote command's stderr)
/// into a caller-facing string. The contract: a message returned to a caller must
/// NEVER contain a PEM private-key block, a password/token/secret assignment, or
/// an <c>Authorization:</c> header value that happened to appear in a command's
/// output.
///
/// <para>
/// A read-only diagnostic command should not emit secrets, but the cost of a
/// single leak (an SSH identity key, a service token) is catastrophic, so we fail
/// safe: any line that looks like PEM key material or a credential is redacted
/// before the text ever leaves the process, and the whole message is length-capped
/// so a pathological dump cannot blow up the response.
/// </para>
///
/// <para>
/// SCOPE: this scrubber is applied to <c>execute-command</c>'s surfaced
/// <c>stderr</c> (a diagnostic channel). It is deliberately NOT applied to
/// <c>read_file</c>'s successful <c>content</c>: for a file read, the path
/// denylist (<see cref="ReadFilePolicy"/>) is the bound that decides whether a
/// file may be disclosed at all — once a path clears the policy, its bytes are
/// returned verbatim (scrubbing a legitimately-requested file would corrupt it).
/// </para>
/// </summary>
internal static class SshgwErrors
{
    /// <summary>Maximum length of a surfaced (stderr) string handed back to a caller.</summary>
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
    /// upstream string before it is surfaced to a caller. Returns <c>null</c> for
    /// null/empty/whitespace input so a caller can preserve an "empty stderr"
    /// distinction (an absent field rather than a placeholder).
    /// </summary>
    internal static string? Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

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
