using System.Text.RegularExpressions;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IPriorSource"/> — derives the org-chart / filename candidate set for a recording
/// DETERMINISTICALLY from two grounded inputs, with NO network and NO LLM (discovery Q6: "v1 ships
/// with filename + manifest participants; a structured org-chart source is additive later behind the
/// same seam"):
/// <list type="number">
///   <item><b>The recording id's filename slug.</b> The Watcher's id is <c>{yyyyMMdd}-{slug(basename)}</c>
///     (<c>Cervello.Watcher.Normalize.Normalizer</c>). This adapter strips the date prefix and mines
///     the remaining hyphen-separated slug tokens for person slugs.</item>
///   <item><b>The manifest §8 entry's optional <c>participants:</c> list</b> (a YAML flow/block list of
///     person slugs the operator/normalize step recorded for the recording). Absent → filename only.</item>
/// </list>
///
/// <para><b>Grounding — never invent a person (the never-guess floor).</b> Every candidate token is
/// admitted ONLY if it resolves to an existing dossier <c>map/people/&lt;slug&gt;.md</c> in the CT
/// working tree. A filename token that matches no dossier is DROPPED (a mis-heard basename never
/// fabricates an identity). An empty candidate set means "no prior identifies this speaker" — the
/// decision policy then relies on the voice match alone (and escalates/omits per its bands).</para>
///
/// <para><b>Strength.</b> The prior is STRONG when a grounded candidate came from the manifest
/// <c>participants:</c> list (an explicit, operator-recorded roster) — that is the signal the decision
/// policy uses to CONFLICT-escalate a high voice match that disagrees with a strong prior (DESIGN
/// §5.1). A filename-only candidate is a weak (non-strong) prior: it narrows but never conflicts.
/// A prior NEVER resolves an identity by itself.</para>
/// </summary>
public sealed class ManifestPriorSource : IPriorSource
{
    // id = {yyyyMMdd}-{slug}; strip the leading 8-digit date + hyphen to get the filename slug tail.
    private static readonly Regex DatePrefix = new(@"^\d{8}-", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A manifest §8 `participants:` entry: an inline flow list `[a, b]` or a block list of `- slug`.
    private static readonly Regex ParticipantsInline = new(
        @"(?m)^\s*participants:\s*\[(?<items>[^\]]*)\]\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _repoRoot;

    /// <param name="repoWorkingTree">
    /// The CT-local <c>ste/cervello</c> working tree (holds <c>recordings/manifest.yaml</c> +
    /// <c>map/people/</c>). Same root the other repo-backed adapters use.
    /// </param>
    public ManifestPriorSource(string repoWorkingTree)
    {
        if (string.IsNullOrWhiteSpace(repoWorkingTree))
            throw new ArgumentException("repoWorkingTree must be non-empty", nameof(repoWorkingTree));
        _repoRoot = repoWorkingTree;
    }

    public Task<PriorCandidates> GetPriorAsync(string recordingId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));

        // ── manifest participants (strong signal) ─────────────────────────────────────────────
        var manifestParticipants = ReadManifestParticipants(recordingId);
        var groundedManifest = manifestParticipants.Where(DossierExists).Distinct(StringComparer.Ordinal).ToList();

        // ── filename slug tokens (weak signal) ────────────────────────────────────────────────
        var filenameCandidates = FilenameTokens(recordingId).Where(DossierExists);

        // Union, manifest first (deterministic, stable order), deduped.
        var candidates = new List<string>(groundedManifest);
        foreach (var c in filenameCandidates)
            if (!candidates.Contains(c, StringComparer.Ordinal))
                candidates.Add(c);

        // Strong iff at least one grounded candidate came from the explicit manifest roster.
        var isStrong = groundedManifest.Count > 0;

        return Task.FromResult(candidates.Count == 0
            ? PriorCandidates.None
            : new PriorCandidates(candidates, isStrong));
    }

    /// <summary>Hyphen-separated tokens of the filename slug (id minus the yyyyMMdd- prefix).</summary>
    private static IEnumerable<string> FilenameTokens(string recordingId)
    {
        var slug = DatePrefix.Replace(recordingId, "");
        if (slug.Length == 0)
            yield break;
        foreach (var token in slug.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return token.ToLowerInvariant();
    }

    /// <summary>
    /// Read the <c>participants:</c> slugs for <paramref name="recordingId"/> from the manifest §8
    /// block, if present. Tolerant hand-parse (the manifest is hand-rolled YAML, matching
    /// <c>YamlManifestStore</c>): locate the <c>- id: &lt;recordingId&gt;</c> block, then read a
    /// <c>participants:</c> line (inline flow list or a following block list) within that block.
    /// Absent / unparseable → empty (filename-only prior; never a throw, never invention).
    /// </summary>
    private IReadOnlyList<string> ReadManifestParticipants(string recordingId)
    {
        var manifestPath = Path.Combine(_repoRoot, "recordings", "manifest.yaml");
        if (!File.Exists(manifestPath))
            return Array.Empty<string>();

        string text;
        try { text = File.ReadAllText(manifestPath).Replace("\r\n", "\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }

        var lines = text.Split('\n');
        // Find the list item whose `id:` equals recordingId, then scan its block (until the next `- ` item).
        var idPattern = new Regex(@"^\s*-\s*id:\s*" + Regex.Escape(recordingId) + @"\s*$",
            RegexOptions.CultureInvariant);
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
            if (idPattern.IsMatch(lines[i])) { start = i; break; }
        if (start < 0)
            return Array.Empty<string>();

        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            // End of this entry's block: a new top-level list item.
            if (Regex.IsMatch(line, @"^\s*-\s*id:\s")) break;

            var inline = ParticipantsInline.Match(line);
            if (inline.Success)
                return SplitSlugs(inline.Groups["items"].Value);

            if (Regex.IsMatch(line, @"^\s*participants:\s*$"))
            {
                // Block list: read the following `- slug` lines (deeper indent than `participants:`).
                var result = new List<string>();
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var item = Regex.Match(lines[j], @"^\s+-\s*(?<slug>[A-Za-z0-9][A-Za-z0-9_-]*)\s*$");
                    if (!item.Success) break;
                    result.Add(item.Groups["slug"].Value.ToLowerInvariant());
                }
                return result;
            }
        }
        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> SplitSlugs(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim('"', '\'').ToLowerInvariant())
            .Where(s => s.Length > 0 && Regex.IsMatch(s, @"^[a-z0-9][a-z0-9_-]*$"))
            .ToList();

    /// <summary>A candidate is grounded iff a person dossier exists at <c>map/people/&lt;slug&gt;.md</c>.</summary>
    private bool DossierExists(string slug) =>
        !string.IsNullOrWhiteSpace(slug)
        && File.Exists(Path.Combine(_repoRoot, "map", "people", $"{slug}.md"));
}
