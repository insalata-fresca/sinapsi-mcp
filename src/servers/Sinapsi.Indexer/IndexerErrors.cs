// ---------------------------------------------------------------------------
// IndexerErrors - uniform, fail-safe sanitization of any error string the
// indexer tools surface to a caller. Plain-ASCII banner so this source diffs
// as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.Text.RegularExpressions;

namespace Sinapsi.Indexer;

/// <summary>
/// Centralises how the indexer tools turn an upstream failure (a Postgres error,
/// an embedding failure, a NATS publish error) into a structured error string.
/// The contract: an error message returned to a caller must NEVER contain a
/// database password, a forge/NATS token, a bearer/authorization value, or any
/// other secret that could appear in an exception's message — and it must be
/// length-capped so a pathological dump cannot blow up the response.
///
/// <para>
/// The connection strings in this server carry <c>INDEXER_DB_PASSWORD</c> and the
/// clone URLs carry <c>FORGE_REPO_TOKEN</c>; an Npgsql / git error can echo those
/// back. We fail safe: any credential-shaped assignment or PEM key block is
/// redacted before the message ever leaves the process. Any status verdict must
/// be computed on the RAW output first (this only shapes the surfaced string).
/// </para>
/// </summary>
internal static class IndexerErrors
{
    /// <summary>Maximum length of an error message handed back to a caller.</summary>
    internal const int MaxErrorLength = 2_000;

    private const string Redacted = "[redacted]";

    // A PEM private-key block (RSA/EC/PKCS#8/OpenSSH) - redact the whole block.
    private static readonly Regex PrivateKeyBlock = new(
        "-----BEGIN [^-]*PRIVATE KEY-----.*?-----END [^-]*PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // key=value / "key": "value" / key: value secrets keyed on a sensitive name.
    // The value is redacted to end-of-line so multi-token credentials are fully
    // covered, not just the first token. The key name is preserved for
    // diagnosability.
    private static readonly Regex SecretAssignment = new(
        @"(?i)\b(password|passwd|secret|token|api[-_]?key|bearer|authorization)\b\s*[:=]\s*\S.*",
        RegexOptions.Compiled);

    /// <summary>
    /// Redact key material / credentials and cap the length of an arbitrary
    /// upstream string before it is surfaced to a caller. Never throws.
    /// </summary>
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "indexer operation failed with no diagnostic output";

        var scrubbed = PrivateKeyBlock.Replace(message, Redacted);
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

    /// <summary>
    /// Sanitize an exception's message for surfacing. The exception TYPE is
    /// prefixed (useful, non-sensitive) and the message is scrubbed. Never throws.
    /// </summary>
    internal static string FromException(Exception e) =>
        Sanitize($"{e.GetType().Name}: {e.Message}");
}
