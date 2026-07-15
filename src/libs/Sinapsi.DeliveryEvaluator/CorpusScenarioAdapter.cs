using System.Text.RegularExpressions;

namespace Sinapsi.DeliveryEvaluator;

/// <summary>
/// Adapts a B1 seed-corpus scenario's prose <c>diff_summary</c> into a <see cref="ChangeSet"/> the
/// <see cref="DeterministicRiskClassifier"/> can score — the runnable path Mission B2 uses to grade
/// this evaluator against <c>datasets/risk-rubric/seed-corpus.yaml</c> (home-server <c>docs/64 §4</c>).
///
/// <para><b>Effect, by construction.</b> The corpus <c>diff_summary</c> is authored as
/// "<i>what the change actually does — the effect an evaluator must infer</i>" (corpus schema), so
/// the whole summary is treated as EFFECT content and any path-like tokens in it are lifted into
/// <see cref="FileChange.Path"/>. The classifier only ever RAISES on effect signatures and never
/// lowers on declared-intent prose, so an adversarial scenario that embeds a "safe, auto-merge"
/// title cannot flip the verdict — the co-present effect signature dominates. (The clean structural
/// proof of the untrusted-diff defense uses a real <see cref="UntrustedChangeMetadata"/> field; see
/// the classifier's tests. This prose adapter is the corpus bridge, not that contract.)</para>
/// </summary>
public static class CorpusScenarioAdapter
{
    // A path-like token: something with a directory slash, or a filename with a known extension.
    private static readonly Regex PathLike = new(
        @"(?<![\w./-])(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+|[A-Za-z0-9_-]+\.(?:cs|md|json|ya?ml|py|ts|conf|toml|container|service|sh|rules|env)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Build a <see cref="ChangeSet"/> from a corpus <c>diff_summary</c>.</summary>
    public static ChangeSet ToChangeSet(string diffSummary, string correlationId = "")
    {
        if (string.IsNullOrWhiteSpace(diffSummary))
            return new ChangeSet(Array.Empty<FileChange>(), UntrustedChangeMetadata.None, correlationId);

        var files = new List<FileChange>();

        // Each extracted path becomes a (content-free) touched file, so PATH classification runs.
        foreach (var path in ExtractPaths(diffSummary))
            files.Add(new FileChange(path, ChangeKind.Modified, Array.Empty<string>(), Array.Empty<string>()));

        // One synthetic content-bearing change carries the full effect text for VALUE scanning.
        files.Add(new FileChange(string.Empty, ChangeKind.Modified, new[] { diffSummary }, Array.Empty<string>()));

        return new ChangeSet(files, UntrustedChangeMetadata.None, correlationId);
    }

    /// <summary>The distinct path-like tokens found in a diff summary.</summary>
    public static IReadOnlyList<string> ExtractPaths(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (Match m in PathLike.Matches(text))
        {
            var token = m.Value.Trim().TrimEnd('.', ',', ';', ')');
            // Ignore bare version-ish or sentence tokens without a real path/extension shape.
            if (token.Contains('/') || token.Contains('.'))
                if (seen.Add(token))
                    result.Add(token);
        }
        return result;
    }
}
