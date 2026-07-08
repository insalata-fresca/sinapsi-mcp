using System.Text.RegularExpressions;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IMapGraph"/> — reads the on-CT map graph from the <c>ste/cervello</c> working tree
/// (<c>map/{people,threads,goals}/&lt;slug&gt;.md</c> + <c>map/timeline.md</c>). This is the graph
/// half of the "graph+index backed" pack assembler (design §2). Every read is VERBATIM — a tolerant
/// hand-parse of frontmatter (matching <see cref="ManifestPriorSource"/>'s posture) + the SCHEMAS §4
/// timeline-line grammar — with NO LLM and NO invention (the never-guess floor): a missing dossier
/// yields null, never a fabricated one.
///
/// <para>Confinement: reads git-side markdown only (no audio, no vectors, no personal-data side
/// channel). The working-tree root is CT-local (<c>CERVELLO_REPO_WORKTREE</c>), the same tree the
/// other repo-backed adapters use.</para>
/// </summary>
public sealed class RepoMapGraph : IMapGraph
{
    // SCHEMAS §4: `- YYYY-MM-DD — <fact> [—] [[link]]… — source: <ref>[ <ref>…]`. The em-dash (—)
    // separates fields; `source:` is mandatory (LINT R1) — a line without one is not a movement line.
    private static readonly Regex TimelineLineRe = new(
        @"^\s*-\s*(?<date>\d{4}-\d{2}-\d{2})\s*—\s*(?<rest>.*?)\bsource:\s*(?<source>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WikiLinkRe = new(@"\[\[(?<slug>[^\]]+)\]\]", RegexOptions.Compiled);

    private readonly string _repoRoot;

    public RepoMapGraph(string repoWorkingTree)
    {
        if (string.IsNullOrWhiteSpace(repoWorkingTree))
            throw new ArgumentException("repoWorkingTree must be non-empty", nameof(repoWorkingTree));
        _repoRoot = repoWorkingTree;
    }

    public Task<MapObject?> GetObjectAsync(MapObjectKind kind, string slug, CancellationToken ct = default)
    {
        var path = PathFor(kind, slug);
        var abs = Path.Combine(_repoRoot, path);
        if (!File.Exists(abs)) return Task.FromResult<MapObject?>(null);

        string text;
        try { text = File.ReadAllText(abs).Replace("\r\n", "\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Task.FromResult<MapObject?>(null); }

        var (frontmatter, body) = SplitFrontmatter(text);
        var sources = ExtractSources(text, path);
        return Task.FromResult<MapObject?>(new MapObject(kind, slug, frontmatter, body, sources));
    }

    public Task<IReadOnlyList<TimelineLine>> WalkTimelineAsync(string anchor, string? from, string? to, CancellationToken ct = default)
    {
        var (kind, slug) = ParseAnchor(anchor);
        var lines = new List<TimelineLine>();

        if (kind == MapObjectKind.Goal && slug is not null)
        {
            // A goal anchor reads the goal's ## Movimento section.
            var abs = Path.Combine(_repoRoot, PathFor(MapObjectKind.Goal, slug));
            if (File.Exists(abs)) lines.AddRange(ParseMovementLines(SafeRead(abs)));
        }
        else
        {
            // person/thread/global draw from the global timeline; person/thread filter by [[slug]] link.
            var abs = Path.Combine(_repoRoot, "map", "timeline.md");
            if (File.Exists(abs))
            {
                foreach (var l in ParseMovementLines(SafeRead(abs)))
                {
                    if (slug is null || l.Links.Contains(slug, StringComparer.OrdinalIgnoreCase))
                        lines.Add(l);
                }
            }
        }

        // Date-range filter (inclusive). Reverse-chronological (newest first).
        IEnumerable<TimelineLine> filtered = lines;
        if (!string.IsNullOrWhiteSpace(from)) filtered = filtered.Where(l => string.CompareOrdinal(l.Date, from) >= 0);
        if (!string.IsNullOrWhiteSpace(to)) filtered = filtered.Where(l => string.CompareOrdinal(l.Date, to) <= 0);
        var ordered = filtered.OrderByDescending(l => l.Date, StringComparer.Ordinal).ToList();
        return Task.FromResult<IReadOnlyList<TimelineLine>>(ordered);
    }

    public async Task<IReadOnlyList<GraphNeighbour>> NeighboursAsync(MapObjectKind kind, string slug, CancellationToken ct = default)
    {
        var obj = await GetObjectAsync(kind, slug, ct);
        if (obj is null) return Array.Empty<GraphNeighbour>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<GraphNeighbour>();

        // Frontmatter people:/threads: lists.
        foreach (var (fmKey, nKind) in new[] { ("people", MapObjectKind.Person), ("threads", MapObjectKind.Thread) })
            if (obj.Frontmatter.TryGetValue(fmKey, out var v))
                foreach (var s in SplitList(v))
                    Add(nKind, s);

        // Body [[wiki-links]] — resolve each to the first existing dossier kind.
        foreach (Match m in WikiLinkRe.Matches(obj.BodyMarkdown))
        {
            var s = m.Groups["slug"].Value.Trim();
            foreach (var nKind in new[] { MapObjectKind.Person, MapObjectKind.Thread, MapObjectKind.Goal })
                if (File.Exists(Path.Combine(_repoRoot, PathFor(nKind, s)))) { Add(nKind, s); break; }
        }
        return result;

        void Add(MapObjectKind nKind, string s)
        {
            s = s.Trim();
            if (s.Length == 0 || (nKind == kind && s.Equals(slug, StringComparison.Ordinal))) return;
            if (seen.Add($"{nKind}:{s}")) result.Add(new GraphNeighbour(nKind, s));
        }
    }

    public Task<IReadOnlyList<string>> ListGoalSlugsAsync(CancellationToken ct = default)
    {
        var dir = Path.Combine(_repoRoot, "map", "goals");
        if (!Directory.Exists(dir)) return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        var slugs = Directory.EnumerateFiles(dir, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(s => !string.IsNullOrEmpty(s) && !s!.StartsWith('_'))  // skip _TEMPLATE.md
            .Select(s => s!)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(slugs);
    }

    // ── parsing helpers ─────────────────────────────────────────────────────────────────────────

    private static string PathFor(MapObjectKind kind, string slug) => kind switch
    {
        MapObjectKind.Person => $"map/people/{slug}.md",
        MapObjectKind.Thread => $"map/threads/{slug}.md",
        MapObjectKind.Goal => $"map/goals/{slug}.md",
        MapObjectKind.Timeline => "map/timeline.md",
        _ => $"map/{slug}.md",
    };

    private static (MapObjectKind kind, string? slug) ParseAnchor(string anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor) || anchor.Equals("global", StringComparison.OrdinalIgnoreCase))
            return (MapObjectKind.Timeline, null);
        var idx = anchor.IndexOf(':');
        if (idx < 0) return (MapObjectKind.Timeline, anchor);
        var kind = anchor[..idx].ToLowerInvariant() switch
        {
            "goal" => MapObjectKind.Goal,
            "person" => MapObjectKind.Person,
            "thread" => MapObjectKind.Thread,
            _ => MapObjectKind.Timeline,
        };
        return (kind, anchor[(idx + 1)..]);
    }

    /// <summary>Split YAML frontmatter (between the leading <c>---</c> fences) into a flat key→string map + the body.</summary>
    private static (IReadOnlyDictionary<string, string> fm, string body) SplitFrontmatter(string text)
    {
        var fm = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            return (fm, text);
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (fm, text);
        var block = text[4..end];
        foreach (var line in block.Split('\n'))
        {
            var c = line.IndexOf(':');
            if (c <= 0) continue;
            var key = line[..c].Trim();
            var val = line[(c + 1)..].Trim();
            if (key.Length > 0) fm[key] = val;
        }
        var bodyStart = text.IndexOf('\n', end + 1);
        var body = bodyStart >= 0 ? text[(bodyStart + 1)..] : "";
        return (fm, body.TrimStart('\n'));
    }

    /// <summary>Every SCHEMAS §1 source ref cited anywhere in the file (the object's resolved refs).</summary>
    private static IReadOnlyList<string> ExtractSources(string text, string selfPath)
    {
        var set = new List<string> { selfPath }; // the dossier is a repo-relative ref to itself
        foreach (Match m in Regex.Matches(text, @"\bsource:\s*(?<refs>[^\n]+)"))
            foreach (var token in m.Groups["refs"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (SourceRef.IsResolvableScheme(token) && !set.Contains(token, StringComparer.Ordinal))
                    set.Add(token);
        return set;
    }

    /// <summary>Parse the SCHEMAS §4 movement lines out of a dossier / timeline file (mandatory <c>source:</c>).</summary>
    private static IEnumerable<TimelineLine> ParseMovementLines(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var m = TimelineLineRe.Match(raw);
            if (!m.Success) continue;
            var date = m.Groups["date"].Value;
            var source = m.Groups["source"].Value.Trim();
            if (!SourceRef.IsResolvableScheme(FirstToken(source))) continue; // LINT R1: no valid source → not a line
            var rest = m.Groups["rest"].Value;
            var links = WikiLinkRe.Matches(rest).Select(x => x.Groups["slug"].Value.Trim()).ToList();
            var fact = WikiLinkRe.Replace(rest, "").Trim(' ', '—', '-').Trim();
            yield return new TimelineLine(date, fact, links, source);
        }
    }

    private static string FirstToken(string s) => s.Split(' ', 2, StringSplitOptions.TrimEntries)[0];

    private static IEnumerable<string> SplitList(string v) =>
        v.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string SafeRead(string abs)
    {
        try { return File.ReadAllText(abs); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
