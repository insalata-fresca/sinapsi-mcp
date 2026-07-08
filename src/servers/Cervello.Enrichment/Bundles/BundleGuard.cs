using System.Text.RegularExpressions;

namespace Cervello.Enrichment.Bundles;

/// <summary>
/// The graph-writer / bundle-writer self-check for lint R6 (no raw Gmail bodies) and R7 (no
/// binaries / base64 blobs) — run BEFORE any bundle or map PR is written (LINT.md: "a graph-writer
/// self-check before it opens any map PR"). This is the in-process mirror of the CI
/// <c>cervello-lint</c> R6/R7 rules so a violation is caught at write time, not merge time.
/// </summary>
public static partial class BundleGuard
{
    // R6: RFC-822 header blocks that betray a pasted raw mail body.
    private static readonly string[] Rfc822Markers =
    [
        "Delivered-To:", "Received:", "DKIM-Signature:", "Return-Path:", "Content-Transfer-Encoding:",
    ];

    [GeneratedRegex(@"[A-Za-z0-9+/]{10,}={0,2}", RegexOptions.CultureInvariant)]
    private static partial Regex Base64Run();

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if <paramref name="content"/> contains a
    /// base64 blob &gt; 10 KiB (R7) — the heuristic for an embedded binary/audio/vector dump.
    /// (Text refs, slugs, short hashes are fine.)
    /// </summary>
    public static void EnsureNoBinaries(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        foreach (Match m in Base64Run().Matches(content))
            if (m.Length > 10 * 1024)
                throw new InvalidOperationException(
                    $"lint R7: base64 blob of {m.Length} bytes in bundle/map content — no binaries/embeddings in git");
    }

    /// <summary>
    /// Throws if <paramref name="content"/> looks like a raw Gmail body (R6): RFC-822 header
    /// markers or a wall of &gt; <paramref name="maxQuotedLines"/> verbatim quoted (<c>&gt; </c>)
    /// lines. Applied to map/dossier content (bundles under <c>inbox/</c> are exempt per R6).
    /// </summary>
    public static void EnsureNoRawMailBody(string content, int maxQuotedLines = 20)
    {
        ArgumentNullException.ThrowIfNull(content);
        foreach (var marker in Rfc822Markers)
            if (content.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"lint R6: RFC-822 header '{marker}' in map content — no raw Gmail bodies in the map");

        var quotedRun = 0;
        foreach (var line in content.Split('\n'))
        {
            if (line.TrimStart().StartsWith("> ", StringComparison.Ordinal))
            {
                if (++quotedRun > maxQuotedLines)
                    throw new InvalidOperationException(
                        $"lint R6: > {maxQuotedLines}-line verbatim quoted block in map content — no raw mail bodies");
            }
            else quotedRun = 0;
        }
    }
}
