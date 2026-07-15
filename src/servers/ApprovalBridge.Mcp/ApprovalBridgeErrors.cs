using System.Text.RegularExpressions;

namespace ApprovalBridge.Mcp;

/// <summary>
/// Centralises how a broker-call failure becomes a caller-facing error string. The broker itself
/// never hands this tool a secret (docs/66 §4, I2 — the seal is structural: the broker never holds
/// a target secret in the first place), but a transport-level failure (a connection error, a
/// misconfigured URL, an unexpected 5xx body from a fronting proxy) could still echo something we
/// would rather not forward verbatim. This mirrors the same redact + length-cap contract as
/// <c>InfisicalErrors</c> / <c>StepCaErrors</c> elsewhere in this repo, applied defensively even
/// though this domain has no secret of its own to leak.
/// </summary>
internal static class ApprovalBridgeErrors
{
    /// <summary>Maximum length of an error message handed back to a caller.</summary>
    internal const int MaxErrorLength = 2_000;

    private const string Redacted = "[redacted]";

    // key=value / "key": "value" secrets keyed on a sensitive-looking name — same pattern as the
    // sibling *.Errors classes, applied defensively to any transport-level error body.
    private static readonly Regex SecretAssignment = new(
        @"(?i)\b(password|passwd|secret|secretValue|token|accessToken|api[-_]?key|bearer|authorization|nonce)\b""?\s*[:=]\s*""?\S.*",
        RegexOptions.Compiled);

    /// <summary>
    /// Redact anything that looks like a credential and cap the length of an arbitrary
    /// transport/upstream string before it is surfaced to a caller. Returns a generic sentinel
    /// for null/blank input.
    /// </summary>
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "approval bridge request failed with no diagnostic output";

        var scrubbed = SecretAssignment.Replace(message, m =>
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
