using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// ContextPackAssembler — THE CORE (design §2, "the delicate context-engineering"). It assembles the
// bounded, ranked, SOURCED cervello context pack the bridge's cervello_context_pack tool calls.
// Everything here is SERVER-SIDE (design §2.5): the Project never fetches raw and trims in-context.
//
// The assembly contract, per §2.1 / §2.5:
//   1. SELECT per-intent (§2.1 table): each intent has a distinct set of sections it assembles —
//      goal_reasoning / portfolio / person_prep / recall / thread. The map GRAPH + the INDEXER are
//      the two sources; nothing else. Every item carries a resolvable source ref (§2.1: no source →
//      not in the pack) — enforced by the PackItem ctor.
//   2. RANK (§2.5): recent-evidence items are ordered by relevance (hybrid-search score) × recency ×
//      graph-proximity to focus. Structural sections (the focus object, its timeline, neighbours)
//      come first by construction (they ARE the focus); ranked evidence fills after.
//   3. BOUND (§2.5): fill to `budget` chars in section order; STOP at a section boundary — never
//      truncate mid-item (same discipline as get_context_pack). A section that would overflow is
//      either summarised (below) or deferred (named in coverage.deferred), never half-included.
//   4. SUMMARISE (§2.5): an over-budget item is replaced by a server-authored summary that INHERITS
//      the item's source ref — bounding never severs provenance.
//   5. COVERAGE (§2.1, mandatory): looked_at / deferred / gaps — the antidote to confabulation. gaps
//      names what Claude must NOT claim beyond ("no recording since … mentions <focus>").
//   6. OPEN_POINTS piggyback (§2.1): pending open-points relevant to focus fold into every pack.
//   7. DELTA (§2.6): goal_reasoning + portfolio carry movement-since-last-look, diffed against the
//      caller's server-side baseline cursor; the baseline then advances.
//   8. DISAMBIGUATION (§2.1): a recall focus that resolves to >1 map entity yields candidates, not a
//      guess (the never-guess floor at read time).
//
// The assembler opens no NATS, no direct DB — it composes over the injected ports (index/graph/
// open-points/cursor), so it is fully unit-testable against fakes with NO personal data.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The parsed, validated request for a context pack (design §5.1).</summary>
public sealed record PackRequest(PackIntent Intent, string? Focus, int Budget, string? Since, string CallerKey);

/// <summary>Assembles the cervello context pack server-side (design §2). The mission's core.</summary>
public sealed class ContextPackAssembler
{
    /// <summary>Default char budget (design §5.1: 30000). Tunable per request + via the host default.</summary>
    public const int DefaultBudget = 30_000;

    // How many ranked recent-evidence items to consider before bounding (indexer limit is clamped 1..30).
    private const int RecentEvidenceLimit = 12;
    // A single item longer than this fraction of the remaining budget is summarised, not included whole.
    private const int MaxItemChars = 1_200;

    private readonly IIndexerSearch _index;
    private readonly IMapGraph _graph;
    private readonly IOpenPointStore _openPoints;
    private readonly IDeltaCursorStore _cursor;
    private readonly ILogger _log;

    public ContextPackAssembler(
        IIndexerSearch index,
        IMapGraph graph,
        IOpenPointStore openPoints,
        IDeltaCursorStore cursor,
        ILogger<ContextPackAssembler>? logger = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _openPoints = openPoints ?? throw new ArgumentNullException(nameof(openPoints));
        _cursor = cursor ?? throw new ArgumentNullException(nameof(cursor));
        _log = logger ?? NullLogger<ContextPackAssembler>.Instance;
    }

    /// <summary>Assemble the pack for a request. The single public entry (design §5.1 <c>POST /context-pack</c>).</summary>
    public async Task<ContextPack> AssembleAsync(PackRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        var asOf = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var lookedAt = new List<string>();
        var deferred = new List<string>();
        var gaps = new List<string>();

        // 1–4: per-intent selection + ranking; bounding + summarisation are applied by Bound() below.
        IReadOnlyList<PackSection> raw = req.Intent switch
        {
            PackIntent.GoalReasoning => await AssembleGoalReasoningAsync(req, lookedAt, gaps, ct),
            PackIntent.Portfolio     => await AssemblePortfolioAsync(req, lookedAt, gaps, ct),
            PackIntent.PersonPrep    => await AssemblePersonPrepAsync(req, lookedAt, gaps, ct),
            PackIntent.Recall        => await AssembleRecallAsync(req, lookedAt, gaps, ct),
            PackIntent.Thread        => await AssembleThreadAsync(req, lookedAt, gaps, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(req), req.Intent, "unknown pack intent"),
        };

        var (bounded, used) = Bound(raw, req.Budget, deferred);

        // 6: open-points piggyback (relevant to focus).
        var openPoints = await PiggybackOpenPointsAsync(req, lookedAt, ct);

        // 7: delta (goal_reasoning + portfolio only) vs the caller's server-side baseline.
        PackDelta? delta = null;
        if (req.Intent is PackIntent.GoalReasoning or PackIntent.Portfolio)
            delta = await ComputeDeltaAsync(req, asOf, ct);

        // 8: disambiguation (recall only, when the focus resolves to >1 entity).
        IReadOnlyList<DisambiguationCandidate>? disambiguation = null;
        if (req.Intent == PackIntent.Recall && req.Focus is { Length: > 0 })
            disambiguation = await DisambiguateAsync(req.Focus, ct);

        return new ContextPack
        {
            Intent = req.Intent,
            Focus = req.Focus,
            Budget = req.Budget,
            Used = used,
            AsOf = asOf,
            Sections = bounded,
            Coverage = new PackCoverage(Dedup(lookedAt), Dedup(deferred), Dedup(gaps)),
            OpenPoints = openPoints,
            Delta = delta,
            Disambiguation = disambiguation is { Count: > 0 } ? disambiguation : null,
        };
    }

    // ── §2.1 per-intent shapes ────────────────────────────────────────────────────────────────

    /// <summary>goal_reasoning (§2.1): goal object + its evidence timeline + top-N ranked evidence + 1-hop neighbours.</summary>
    private async Task<IReadOnlyList<PackSection>> AssembleGoalReasoningAsync(
        PackRequest req, List<string> lookedAt, List<string> gaps, CancellationToken ct)
    {
        var sections = new List<PackSection>();
        var slug = RequireFocus(req, "goal_reasoning");
        lookedAt.Add("map/goals");

        var goal = await _graph.GetObjectAsync(MapObjectKind.Goal, slug, ct);
        if (goal is null)
        {
            gaps.Add($"no goal dossier map/goals/{slug}.md exists — cannot reason about its movement");
            return sections;
        }

        // (1) the goal object (frontmatter + ## Stato), sourced by its own path.
        sections.Add(Section("goal", new[] { ObjectItem(goal) }));

        // (2) its evidence timeline — the goal's dated, sourced ## Movimento lines.
        var movimento = await _graph.WalkTimelineAsync($"goal:{slug}", req.Since, null, ct);
        if (movimento.Count > 0)
            sections.Add(Section("evidence_timeline", movimento.Select(TimelineItem).ToList()));
        else
            gaps.Add($"goal '{slug}' has no ## Movimento evidence lines yet");

        // (3) top-N recent evidence — ranked against the goal text + linked entities, newest-weighted.
        lookedAt.Add("index:recordings");
        var evidence = await RankedEvidenceAsync(GoalQuery(goal), goal.Frontmatter, ct);
        if (evidence.Count > 0)
            sections.Add(Section("recent_evidence", evidence));
        else
            gaps.Add($"no indexed evidence ranks against goal '{slug}'");

        // (4) involved people/threads — 1-hop graph neighbours (their Stato / Decisioni & next).
        var neighbourSection = await NeighbourSectionAsync(MapObjectKind.Goal, slug, lookedAt, ct);
        if (neighbourSection is not null) sections.Add(neighbourSection);

        return sections;
    }

    /// <summary>portfolio (§2.1): each active goal's frontmatter + status + last movement line + open next-steps; shallow + wide.</summary>
    private async Task<IReadOnlyList<PackSection>> AssemblePortfolioAsync(
        PackRequest req, List<string> lookedAt, List<string> gaps, CancellationToken ct)
    {
        lookedAt.Add("map/goals");
        var slugs = await _graph.ListGoalSlugsAsync(ct);
        var items = new List<PackItem>();
        foreach (var slug in slugs)
        {
            var goal = await _graph.GetObjectAsync(MapObjectKind.Goal, slug, ct);
            if (goal is null) continue;
            // Portfolio is deliberately shallow: status + last movement line only, not the full timeline.
            var status = goal.Frontmatter.TryGetValue("status", out var s) ? s : "active";
            if (status is "dropped" or "achieved") continue; // "active goals" sweep (design §2.1 portfolio)
            var movimento = await _graph.WalkTimelineAsync($"goal:{slug}", null, null, ct);
            var last = movimento.Count > 0
                ? $"{goal.Frontmatter.GetValueOrDefault("name", slug)} [{status}] — last: {movimento[0].Date} {movimento[0].Fact}"
                : $"{goal.Frontmatter.GetValueOrDefault("name", slug)} [{status}] — no movement yet";
            var src = movimento.Count > 0 ? movimento[0].Source : goal.Sources.FirstOrDefault() ?? goal.Id + ".md";
            items.Add(new PackItem(last, ResolvableOrPath(src, goal), null));
        }
        var sections = new List<PackSection>();
        if (items.Count > 0) sections.Add(Section("portfolio", items));
        else gaps.Add("no active goals in map/goals");
        return sections;
    }

    /// <summary>person_prep (§2.1): dossier + linked threads + last-N interactions + evidence-linked goals.</summary>
    private async Task<IReadOnlyList<PackSection>> AssemblePersonPrepAsync(
        PackRequest req, List<string> lookedAt, List<string> gaps, CancellationToken ct)
    {
        var sections = new List<PackSection>();
        var slug = RequireFocus(req, "person_prep");
        lookedAt.Add($"map/people/{slug}");

        var person = await _graph.GetObjectAsync(MapObjectKind.Person, slug, ct);
        if (person is null)
        {
            gaps.Add($"no person dossier map/people/{slug}.md exists");
            return sections;
        }
        sections.Add(Section("person", new[] { ObjectItem(person) }));

        // linked threads (their Stato / Decisioni & next).
        var neighbourSection = await NeighbourSectionAsync(MapObjectKind.Person, slug, lookedAt, ct);
        if (neighbourSection is not null) sections.Add(neighbourSection);

        // last-N interactions — timeline lines mentioning the person, newest first.
        lookedAt.Add("map/timeline.md");
        var interactions = await _graph.WalkTimelineAsync($"person:{slug}", req.Since, null, ct);
        if (interactions.Count > 0)
            sections.Add(Section("interactions", interactions.Take(RecentEvidenceLimit).Select(TimelineItem).ToList()));
        else
            gaps.Add($"no timeline interactions mention '{slug}'");

        return sections;
    }

    /// <summary>recall (§2.1): hybrid-search top-N across the corpus + the resolved map entity (if any) with its dossier.</summary>
    private async Task<IReadOnlyList<PackSection>> AssembleRecallAsync(
        PackRequest req, List<string> lookedAt, List<string> gaps, CancellationToken ct)
    {
        var sections = new List<PackSection>();
        var question = RequireFocus(req, "recall");
        lookedAt.Add("index:all");

        var hits = await _index.SearchAsync(question, kind: null, limit: RecentEvidenceLimit, ct);
        var ranked = RankHits(hits, focusFrontmatter: null);
        if (ranked.Count > 0)
            sections.Add(Section("recall", ranked.Select(HitItem).ToList()));
        else
            gaps.Add($"nothing in the index matches '{question}'");

        // If the question resolves to exactly one map entity, include its dossier (disambiguation handles >1).
        var resolved = await ResolveEntityAsync(question, ct);
        if (resolved is { Count: 1 })
        {
            var obj = await _graph.GetObjectAsync(resolved[0].Kind, resolved[0].Slug, ct);
            if (obj is not null)
            {
                lookedAt.Add($"map/{resolved[0].Kind.ToString().ToLowerInvariant()}s/{resolved[0].Slug}");
                sections.Add(Section("entity", new[] { ObjectItem(obj) }));
            }
        }
        return sections;
    }

    /// <summary>thread (§2.1): thread dossier full body + its people's one-liners + its timeline tail + linked goals.</summary>
    private async Task<IReadOnlyList<PackSection>> AssembleThreadAsync(
        PackRequest req, List<string> lookedAt, List<string> gaps, CancellationToken ct)
    {
        var sections = new List<PackSection>();
        var slug = RequireFocus(req, "thread");
        lookedAt.Add($"map/threads/{slug}");

        var thread = await _graph.GetObjectAsync(MapObjectKind.Thread, slug, ct);
        if (thread is null)
        {
            gaps.Add($"no thread dossier map/threads/{slug}.md exists");
            return sections;
        }
        sections.Add(Section("thread", new[] { ObjectItem(thread) }));

        var neighbourSection = await NeighbourSectionAsync(MapObjectKind.Thread, slug, lookedAt, ct);
        if (neighbourSection is not null) sections.Add(neighbourSection);

        var tail = await _graph.WalkTimelineAsync($"thread:{slug}", req.Since, null, ct);
        if (tail.Count > 0)
            sections.Add(Section("timeline_tail", tail.Take(RecentEvidenceLimit).Select(TimelineItem).ToList()));

        return sections;
    }

    // ── ranking (§2.5): relevance × recency × graph-proximity ───────────────────────────────────

    /// <summary>Rank indexer hits for a focus, then project to sourced pack items.</summary>
    private async Task<IReadOnlyList<PackItem>> RankedEvidenceAsync(
        string query, IReadOnlyDictionary<string, string> focusFrontmatter, CancellationToken ct)
    {
        var hits = await _index.SearchAsync(query, kind: "recording", limit: RecentEvidenceLimit, ct);
        return RankHits(hits, focusFrontmatter).Select(HitItem).ToList();
    }

    /// <summary>
    /// The ranking function (§2.5): score = relevance (hybrid rank) × recency-weight × graph-proximity.
    /// Recency weight decays with age; graph-proximity boosts a hit whose path/title names a linked
    /// entity of the focus. Deterministic + pure — testable without a live index.
    /// </summary>
    private static IReadOnlyList<IndexerHit> RankHits(
        IReadOnlyList<IndexerHit> hits, IReadOnlyDictionary<string, string>? focusFrontmatter)
    {
        var linkedTokens = focusFrontmatter is null ? new HashSet<string>() : LinkedTokens(focusFrontmatter);
        return hits
            .Select(h => (h, score: h.Rank * RecencyWeight(h.Date) * ProximityWeight(h, linkedTokens)))
            .OrderByDescending(x => x.score)
            .Select(x => x.h)
            .ToList();
    }

    private static double RecencyWeight(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || !DateOnly.TryParse(date, out var d)) return 1.0;
        var ageDays = System.Math.Max(0, (DateTime.UtcNow.Date - d.ToDateTime(TimeOnly.MinValue)).TotalDays);
        // Half-weight at ~180 days; never below 0.25 (old-but-relevant still ranks).
        return System.Math.Max(0.25, 1.0 / (1.0 + ageDays / 180.0));
    }

    private static double ProximityWeight(IndexerHit h, HashSet<string> linkedTokens)
    {
        if (linkedTokens.Count == 0) return 1.0;
        var hay = (h.Title + " " + h.Path + " " + h.Snippet).ToLowerInvariant();
        return linkedTokens.Any(t => hay.Contains(t, StringComparison.Ordinal)) ? 1.5 : 1.0;
    }

    private static HashSet<string> LinkedTokens(IReadOnlyDictionary<string, string> fm)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in new[] { "people", "threads", "name" })
            if (fm.TryGetValue(key, out var v))
                foreach (var t in v.Split(new[] { ',', '[', ']', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (t.Length > 2) set.Add(t.ToLowerInvariant());
        return set;
    }

    // ── bounding (§2.5): fill to budget in section order; STOP at a section boundary ────────────

    /// <summary>
    /// Fill sections into the budget IN ORDER. A section that fits whole is included; one that would
    /// overflow has its over-long items summarised (inheriting refs); if the summarised section still
    /// overflows the remaining budget, it is DEFERRED (named in coverage.deferred) rather than
    /// half-included — never truncate mid-item (design §2.5). Returns the bounded sections + used chars.
    /// </summary>
    private static (IReadOnlyList<PackSection> sections, int used) Bound(
        IReadOnlyList<PackSection> raw, int budget, List<string> deferred)
    {
        var kept = new List<PackSection>();
        var used = 0;
        foreach (var section in raw)
        {
            var remaining = budget - used;
            if (remaining <= 0)
            {
                deferred.Add($"{section.Section} (out of budget)");
                continue;
            }
            // Summarise over-long items first (they inherit their source ref).
            var fitted = new List<PackItem>();
            var sectionCost = 0;
            var overflowed = false;
            foreach (var item in section.Items)
            {
                var candidate = item.Cost > MaxItemChars ? Summarise(item) : item;
                if (sectionCost + candidate.Cost > remaining)
                {
                    overflowed = true;
                    break; // STOP at a boundary — do not truncate this item.
                }
                fitted.Add(candidate);
                sectionCost += candidate.Cost;
            }
            if (fitted.Count == 0)
            {
                deferred.Add($"{section.Section} (out of budget)");
                continue;
            }
            if (overflowed)
                deferred.Add($"{section.Section} (partially included — {section.Items.Count - fitted.Count} item(s) deferred, out of budget)");
            kept.Add(section with { Items = fitted });
            used += sectionCost;
        }
        return (kept, used);
    }

    /// <summary>
    /// Replace an over-long item with a server-authored summary that INHERITS the item's source ref
    /// (design §2.5: summarisation never severs provenance). The summary is a head-truncation with an
    /// explicit elision marker — deterministic, never an LLM hallucination.
    /// </summary>
    private static PackItem Summarise(PackItem item)
    {
        var head = item.Content.Length > MaxItemChars ? item.Content[..MaxItemChars].TrimEnd() : item.Content;
        return new PackItem($"{head} … [summarised, source preserved]", item.Source, item.Confidence);
    }

    // ── open-points piggyback (§2.1) ────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<PackOpenPoint>> PiggybackOpenPointsAsync(
        PackRequest req, List<string> lookedAt, CancellationToken ct)
    {
        // Focus-relevant filter: a rec:// focus filters by recording; otherwise all pending fold in.
        string? recording = null;
        if (req.Focus is { Length: > 0 } f && f.StartsWith("rec://", StringComparison.Ordinal))
            recording = f["rec://".Length..];
        var pending = await _openPoints.ListPendingAsync(recording, ct);
        if (pending.Count > 0) lookedAt.Add("open_points");
        return pending.Select(p => { var v = OpenPointView.From(p); return new PackOpenPoint(v.PointId, v.Question, v.KindWire); }).ToList();
    }

    // ── delta (§2.6): per-caller baseline diff ──────────────────────────────────────────────────

    private async Task<PackDelta?> ComputeDeltaAsync(PackRequest req, string asOf, CancellationToken ct)
    {
        var intentKey = req.Intent.ToString();
        var baseline = req.Since ?? await _cursor.GetBaselineAsync(req.CallerKey, intentKey, ct);
        if (string.IsNullOrWhiteSpace(baseline))
        {
            // First sweep — no baseline yet. Record it and return an empty delta so the caller sees "no prior look".
            await _cursor.AdvanceAsync(req.CallerKey, intentKey, asOf, ct);
            return new PackDelta(asOf, Array.Empty<DeltaEvidence>(), Array.Empty<DeltaStatusChange>());
        }

        var newEvidence = new List<DeltaEvidence>();
        // Diff the movement lines since the baseline across the relevant scope.
        var anchors = req.Intent == PackIntent.GoalReasoning && req.Focus is { Length: > 0 }
            ? new[] { $"goal:{req.Focus}" }
            : (await _graph.ListGoalSlugsAsync(ct)).Select(s => $"goal:{s}").ToArray();
        foreach (var anchor in anchors)
        {
            var lines = await _graph.WalkTimelineAsync(anchor, from: baseline, to: null, ct);
            foreach (var l in lines)
                newEvidence.Add(new DeltaEvidence(l.Date, l.Fact, l.Source));
        }

        await _cursor.AdvanceAsync(req.CallerKey, intentKey, asOf, ct);
        return new PackDelta(baseline, newEvidence, Array.Empty<DeltaStatusChange>());
    }

    // ── disambiguation (§2.1, recall) ───────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<DisambiguationCandidate>?> DisambiguateAsync(string focus, CancellationToken ct)
    {
        var resolved = await ResolveEntityAsync(focus, ct);
        if (resolved is not { Count: > 1 }) return null;
        var cands = new List<DisambiguationCandidate>();
        foreach (var r in resolved)
        {
            var obj = await _graph.GetObjectAsync(r.Kind, r.Slug, ct);
            var descriptor = obj?.Frontmatter.GetValueOrDefault("name", r.Slug) ?? r.Slug;
            cands.Add(new DisambiguationCandidate($"{r.Kind.ToString().ToLowerInvariant()}:{r.Slug}", descriptor, obj?.Sources.Count ?? 0));
        }
        return cands;
    }

    /// <summary>Resolve a free-text focus to candidate map entities via the index (kind-tagged hits with a map path).</summary>
    private async Task<IReadOnlyList<GraphNeighbour>> ResolveEntityAsync(string focus, CancellationToken ct)
    {
        var hits = await _index.SearchAsync(focus, kind: null, limit: 8, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<GraphNeighbour>();
        foreach (var h in hits)
        {
            var kind = MapKindOf(h.Path, h.Kind);
            if (kind is null) continue;
            var slug = SlugOf(h.Path);
            if (slug is null || !seen.Add($"{kind}:{slug}")) continue;
            result.Add(new GraphNeighbour(kind.Value, slug));
        }
        return result;
    }

    // ── neighbour section (goal_reasoning / person_prep / thread): 1-hop neighbours' Stato ─────────

    private async Task<PackSection?> NeighbourSectionAsync(
        MapObjectKind kind, string slug, List<string> lookedAt, CancellationToken ct)
    {
        var neighbours = await _graph.NeighboursAsync(kind, slug, ct);
        var items = new List<PackItem>();
        foreach (var n in neighbours)
        {
            var obj = await _graph.GetObjectAsync(n.Kind, n.Slug, ct);
            if (obj is null) continue;
            lookedAt.Add($"map/{n.Kind.ToString().ToLowerInvariant()}s/{n.Slug}");
            var name = obj.Frontmatter.GetValueOrDefault("name", n.Slug);
            var stato = ExtractSection(obj.BodyMarkdown, "## Stato") ?? ExtractSection(obj.BodyMarkdown, "## Decisioni & next") ?? "";
            var content = $"{name} ({n.Kind.ToString().ToLowerInvariant()}): {stato}".Trim();
            items.Add(new PackItem(content, ResolvableOrPath(obj.Sources.FirstOrDefault() ?? obj.Id, obj), null));
        }
        return items.Count == 0 ? null : Section("involved", items);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static PackSection Section(string name, IReadOnlyList<PackItem> items) => new(name, items);

    private static PackItem ObjectItem(MapObject o)
    {
        var name = o.Frontmatter.GetValueOrDefault("name", o.Id);
        var stato = ExtractSection(o.BodyMarkdown, "## Stato");
        var content = stato is null ? $"{name}\n{o.BodyMarkdown}" : $"{name} — {stato}";
        return new PackItem(content, ResolvableOrPath(o.Sources.FirstOrDefault() ?? PathOf(o), o), null);
    }

    private static PackItem TimelineItem(TimelineLine l) =>
        new($"{l.Date} — {l.Fact}", l.Source, null);

    private static PackItem HitItem(IndexerHit h) =>
        new(string.IsNullOrWhiteSpace(h.Snippet) ? h.Title : $"{h.Title}: {h.Snippet}", h.Source, h.Rank <= 1.0 ? h.Rank : null);

    /// <summary>A goal's index query = its name + tags + linked people/threads (the entities it's about).</summary>
    private static string GoalQuery(MapObject goal)
    {
        var parts = new List<string> { goal.Frontmatter.GetValueOrDefault("name", goal.Id) };
        foreach (var k in new[] { "tags", "people", "threads" })
            if (goal.Frontmatter.TryGetValue(k, out var v)) parts.Add(v.Trim('[', ']'));
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string PathOf(MapObject o) => o.Kind switch
    {
        MapObjectKind.Person => $"map/people/{o.Id}.md",
        MapObjectKind.Thread => $"map/threads/{o.Id}.md",
        MapObjectKind.Goal => $"map/goals/{o.Id}.md",
        _ => "map/timeline.md",
    };

    /// <summary>Ensure the ref is a valid SCHEMAS §1 ref; fall back to the object's repo-relative path.</summary>
    private static string ResolvableOrPath(string candidate, MapObject o) =>
        SourceRef.IsResolvableScheme(candidate) ? candidate : PathOf(o);

    private static MapObjectKind? MapKindOf(string path, string kind)
    {
        if (path.Contains("map/people/", StringComparison.Ordinal) || kind == "person") return MapObjectKind.Person;
        if (path.Contains("map/threads/", StringComparison.Ordinal) || kind == "thread") return MapObjectKind.Thread;
        if (path.Contains("map/goals/", StringComparison.Ordinal) || kind == "goal") return MapObjectKind.Goal;
        return null;
    }

    private static string? SlugOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var file = System.IO.Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(file) ? null : file;
    }

    /// <summary>Extract a markdown section body (text under a <c>## Heading</c> up to the next <c>## </c>).</summary>
    private static string? ExtractSection(string body, string heading)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimEnd().Equals(heading, StringComparison.Ordinal));
        if (start < 0) return null;
        var collected = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal)) break;
            collected.Add(lines[i]);
        }
        var text = string.Join(" ", collected.Select(l => l.Trim()).Where(l => l.Length > 0));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string RequireFocus(PackRequest req, string intent) =>
        req.Focus is { Length: > 0 } f
            ? f
            : throw new ArgumentException($"intent '{intent}' requires a focus", nameof(req));

    private static IReadOnlyList<string> Dedup(List<string> xs) =>
        xs.Distinct(StringComparer.Ordinal).ToList();
}
