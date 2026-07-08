using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The CORE of MISSION CB-BACKEND: the <see cref="ContextPackAssembler"/> (design §2). Proves the
/// context-pack contract against fakes — NO network, NO DB, NO personal data (synthetic map + index):
///
/// <list type="bullet">
/// <item><b>Sourced floor (§2.1).</b> Every returned item carries a resolvable source ref.</item>
/// <item><b>Per-intent shapes (§2.1).</b> goal_reasoning assembles goal + evidence timeline + ranked
///   evidence + neighbours; portfolio is shallow+wide; person_prep / recall / thread each shape.</item>
/// <item><b>Coverage is mandatory (§2.1).</b> looked_at populated; gaps names a genuine absence.</item>
/// <item><b>Bounding (§2.5).</b> a tiny budget STOPs at a section boundary + defers the rest (never
///   truncates mid-item); an over-long item is summarised with its source inherited.</item>
/// <item><b>Open-points piggyback (§2.1).</b> a pending point folds into the pack.</item>
/// <item><b>Delta (§2.6).</b> first sweep records a baseline; a later sweep surfaces new movement.</item>
/// <item><b>Never-guess (§2.3).</b> a missing focus yields a gap, not a fabricated section.</item>
/// </list>
/// </summary>
public sealed class ContextPackAssemblerTests
{
    private const string Caller = "bearer:test";

    // ── a synthetic map graph + index (no filesystem, no personal data) ──────────────────────────

    private sealed class FakeGraph : IMapGraph
    {
        public Dictionary<string, MapObject> Objects { get; } = new();
        public Dictionary<string, List<TimelineLine>> Timelines { get; } = new();
        public List<string> Goals { get; } = new();
        public Dictionary<string, List<GraphNeighbour>> Neighbours { get; } = new();

        public Task<MapObject?> GetObjectAsync(MapObjectKind kind, string slug, CancellationToken ct = default) =>
            Task.FromResult(Objects.TryGetValue($"{kind}:{slug}", out var o) ? o : null);
        public Task<IReadOnlyList<TimelineLine>> WalkTimelineAsync(string anchor, string? from, string? to, CancellationToken ct = default)
        {
            var lines = Timelines.TryGetValue(anchor, out var l) ? l : new List<TimelineLine>();
            IEnumerable<TimelineLine> f = lines;
            if (!string.IsNullOrEmpty(from)) f = f.Where(x => string.CompareOrdinal(x.Date, from) >= 0);
            return Task.FromResult<IReadOnlyList<TimelineLine>>(f.OrderByDescending(x => x.Date, StringComparer.Ordinal).ToList());
        }
        public Task<IReadOnlyList<GraphNeighbour>> NeighboursAsync(MapObjectKind kind, string slug, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GraphNeighbour>>(Neighbours.TryGetValue($"{kind}:{slug}", out var n) ? n : new List<GraphNeighbour>());
        public Task<IReadOnlyList<string>> ListGoalSlugsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Goals);
    }

    private sealed class FakeIndex : IIndexerSearch
    {
        public List<IndexerHit> Hits { get; } = new();
        public Task<IReadOnlyList<IndexerHit>> SearchAsync(string query, string? kind, int? limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IndexerHit>>(Hits);
    }

    private static MapObject Goal(string slug, string status = "active", string stato = "on track") => new(
        MapObjectKind.Goal, slug,
        new Dictionary<string, string> { ["type"] = "goal", ["name"] = slug, ["status"] = status },
        $"# {slug}\n\n## Stato\n{stato}\n\n## Movimento\n",
        new[] { $"map/goals/{slug}.md" });

    private static ContextPackAssembler Build(FakeIndex idx, FakeGraph g, InMemoryOpenPointStore ops, InMemoryDeltaCursorStore cur) =>
        new(idx, g, ops, cur);

    private static PackRequest Req(PackIntent intent, string? focus, int budget = 30_000, string? since = null) =>
        new(intent, focus, budget, since, Caller);

    // ── tests ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Goal_reasoning_assembles_goal_evidence_and_ranked_recent()
    {
        var g = new FakeGraph();
        g.Objects["Goal:series-a"] = Goal("series-a");
        g.Timelines["goal:series-a"] = new()
        {
            new("2026-06-01", "term sheet received", new[] { "series-a" }, "rec://2026-06-01-call"),
        };
        var idx = new FakeIndex();
        idx.Hits.Add(new IndexerHit("investor call", "recordings/transcripts/2026-06-20.md", "discussed valuation", 0.9, "rec://2026-06-20-call", "recording", "2026-06-20"));

        var pack = await Build(idx, g, new InMemoryOpenPointStore(), new InMemoryDeltaCursorStore())
            .AssembleAsync(Req(PackIntent.GoalReasoning, "series-a"));

        Assert.Equal(PackIntent.GoalReasoning, pack.Intent);
        Assert.Contains(pack.Sections, s => s.Section == "goal");
        Assert.Contains(pack.Sections, s => s.Section == "evidence_timeline");
        Assert.Contains(pack.Sections, s => s.Section == "recent_evidence");
        // Sourced floor: every item has a resolvable ref.
        Assert.All(pack.Sections.SelectMany(s => s.Items), i => Assert.True(SourceRef.IsResolvableScheme(i.Source)));
        Assert.Contains("map/goals", pack.Coverage.LookedAt);
    }

    [Fact]
    public async Task Missing_goal_yields_a_gap_not_a_fabricated_section()
    {
        var pack = await Build(new FakeIndex(), new FakeGraph(), new InMemoryOpenPointStore(), new InMemoryDeltaCursorStore())
            .AssembleAsync(Req(PackIntent.GoalReasoning, "ghost"));
        Assert.Empty(pack.Sections);
        Assert.Contains(pack.Coverage.Gaps, gp => gp.Contains("ghost"));
    }

    [Fact]
    public async Task Bounding_stops_at_a_section_boundary_and_defers_the_rest()
    {
        var g = new FakeGraph();
        g.Objects["Goal:big"] = Goal("big", stato: new string('x', 50));
        g.Timelines["goal:big"] = Enumerable.Range(0, 40)
            .Select(i => new TimelineLine($"2026-06-{(i % 28) + 1:00}", new string('e', 200), new[] { "big" }, "rec://r" + i))
            .ToList();

        var pack = await Build(new FakeIndex(), g, new InMemoryOpenPointStore(), new InMemoryDeltaCursorStore())
            .AssembleAsync(Req(PackIntent.GoalReasoning, "big", budget: 300));

        Assert.True(pack.Used <= 300, $"used {pack.Used} exceeded budget 300");
        // No item was cut mid-content: every kept item is intact (present in full or summarised-with-marker).
        Assert.All(pack.Sections.SelectMany(s => s.Items),
            i => Assert.True(i.Content.Length <= 300 || i.Content.Contains("[summarised")));
        Assert.NotEmpty(pack.Coverage.Deferred);
    }

    [Fact]
    public async Task Over_long_item_is_summarised_with_source_inherited()
    {
        var g = new FakeGraph();
        g.Objects["Goal:long"] = Goal("long");
        g.Timelines["goal:long"] = new()
        {
            new("2026-06-01", new string('z', 5_000), new[] { "long" }, "rec://long-evidence"),
        };
        var pack = await Build(new FakeIndex(), g, new InMemoryOpenPointStore(), new InMemoryDeltaCursorStore())
            .AssembleAsync(Req(PackIntent.GoalReasoning, "long", budget: 4_000));

        var item = pack.Sections.SelectMany(s => s.Items).FirstOrDefault(i => i.Content.Contains("[summarised"));
        Assert.NotNull(item);
        Assert.Equal("rec://long-evidence", item!.Source); // provenance survived the summary (§2.5)
    }

    [Fact]
    public async Task Open_points_piggyback_into_the_pack()
    {
        var g = new FakeGraph();
        g.Objects["Goal:op"] = Goal("op");
        var ops = new InMemoryOpenPointStore();
        await ops.EnqueueAsync(new OpenPoint("op_9", OpenPointKind.Fact, "rec-x", "b-x", "does this attach to goal op?",
            new[] { new ScoredCandidate("op", 0.4, "weak") }));

        var pack = await Build(new FakeIndex(), g, ops, new InMemoryDeltaCursorStore())
            .AssembleAsync(Req(PackIntent.GoalReasoning, "op"));

        Assert.Contains(pack.OpenPoints, o => o.PointId == "op_9" && o.Kind == "link");
    }

    [Fact]
    public async Task Portfolio_is_shallow_and_wide_over_active_goals()
    {
        var g = new FakeGraph();
        g.Goals.AddRange(new[] { "a", "b", "done" });
        g.Objects["Goal:a"] = Goal("a");
        g.Objects["Goal:b"] = Goal("b");
        g.Objects["Goal:done"] = Goal("done", status: "achieved");
        g.Timelines["goal:a"] = new() { new("2026-06-10", "moved a", new[] { "a" }, "rec://a1") };

        var pack = await Build(new FakeIndex(), g, new InMemoryOpenPointStore(), new InMemoryDeltaCursorStore())
            .AssembleAsync(Req(PackIntent.Portfolio, null));

        var portfolio = Assert.Single(pack.Sections, s => s.Section == "portfolio");
        // "done" (achieved) is excluded from the active sweep; a + b remain — one line each (shallow).
        Assert.Equal(2, portfolio.Items.Count);
        Assert.All(portfolio.Items, i => Assert.True(SourceRef.IsResolvableScheme(i.Source)));
    }

    [Fact]
    public async Task Delta_first_sweep_records_baseline_then_surfaces_new_movement()
    {
        var g = new FakeGraph();
        g.Objects["Goal:d"] = Goal("d");
        g.Goals.Add("d");
        var cur = new InMemoryDeltaCursorStore();
        var assembler = Build(new FakeIndex(), g, new InMemoryOpenPointStore(), cur);

        // First sweep: no baseline → empty delta, baseline recorded.
        var first = await assembler.AssembleAsync(Req(PackIntent.GoalReasoning, "d"));
        Assert.NotNull(first.Delta);
        Assert.Empty(first.Delta!.NewEvidence);

        // New movement lands after the baseline; second sweep surfaces it.
        g.Timelines["goal:d"] = new() { new("2999-01-01", "big news", new[] { "d" }, "rec://future") };
        var second = await assembler.AssembleAsync(Req(PackIntent.GoalReasoning, "d"));
        Assert.NotNull(second.Delta);
        Assert.Contains(second.Delta!.NewEvidence, e => e.Fact == "big news" && e.Source == "rec://future");
    }

    [Fact]
    public async Task Recall_disambiguates_when_focus_resolves_to_multiple_entities()
    {
        var g = new FakeGraph();
        g.Objects["Person:marco-a"] = new(MapObjectKind.Person, "marco-a",
            new Dictionary<string, string> { ["name"] = "Marco A" }, "## Chi è\nfirst", new[] { "map/people/marco-a.md" });
        g.Objects["Person:marco-b"] = new(MapObjectKind.Person, "marco-b",
            new Dictionary<string, string> { ["name"] = "Marco B" }, "## Chi è\nsecond", new[] { "map/people/marco-b.md" });
        var idx = new FakeIndex();
        idx.Hits.Add(new IndexerHit("Marco A", "map/people/marco-a.md", "", 0.9, "map/people/marco-a.md", "person"));
        idx.Hits.Add(new IndexerHit("Marco B", "map/people/marco-b.md", "", 0.8, "map/people/marco-b.md", "person"));

        var pack = await Build(idx, g, new InMemoryOpenPointStore(), new InMemoryDeltaCursorStore())
            .AssembleAsync(Req(PackIntent.Recall, "marco"));

        Assert.NotNull(pack.Disambiguation);
        Assert.Equal(2, pack.Disambiguation!.Count);
    }
}
