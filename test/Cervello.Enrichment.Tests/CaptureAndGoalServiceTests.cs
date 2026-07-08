using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The CAPTURE loop (design §5.5) + the GOAL object write path (§5.6/§5.7). Proves confirm-by-default
/// (MC Q6), grounded provenance, pin-on-cite reuse, and the never-delete rule — all against fakes,
/// NO personal data.
///
/// <list type="bullet">
/// <item><b>Capture confirm=false previews, writes nothing (§5.5).</b> confirm=true deposits into
///   conversations/ + inbox/ with a deposit:// source + human:// basis; NEVER into map/.</item>
/// <item><b>set_goal confirm=false previews the exact dossier; confirm=true opens a review-PR (§5.6).</b>
///   status is validated against the MC-ratified vocabulary; the dossier renders type: goal + the four
///   body sections including ## Movimento (§3.1).</item>
/// <item><b>link_evidence appends a sourced ## Movimento line via review-PR (§5.7).</b> a missing
///   source ref is rejected (LINT R1); an external ref is pinned on cite by the graph-writer (R11);
///   an unknown goal → unknown_goal.</item>
/// </list>
/// </summary>
public sealed class CaptureAndGoalServiceTests
{
    private static readonly DateOnly Today = new(2026, 7, 8);

    // ── capture ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Capture_preview_writes_nothing_and_shows_provenance()
    {
        var store = new InMemoryDepositStore();
        var svc = new CaptureService(store);

        var r = await svc.CaptureAsync("Guilhem prefers async standups", "said by Guilhem on 2026-07-01",
            new[] { "person:guilhem" }, confirm: false, Today);

        Assert.Equal("preview", r.Status);
        Assert.Null(r.Commit);
        Assert.StartsWith("deposit://", r.Source);
        Assert.StartsWith("human://", r.Basis);
        Assert.NotNull(r.Preview);
        Assert.False(store.Exists(r.DepositId)); // confirm-by-default: nothing written
    }

    [Fact]
    public async Task Capture_confirm_deposits_into_conversations_and_inbox_never_map()
    {
        var store = new InMemoryDepositStore();
        var r = await new CaptureService(store).CaptureAsync("fact", "hint", Array.Empty<string>(), confirm: true, Today);

        Assert.Equal("deposited", r.Status);
        Assert.NotNull(r.Commit);
        Assert.StartsWith("inbox/", r.Path);
        var rec = store.Get(r.DepositId);
        Assert.NotNull(rec);
        Assert.Contains("deposit://", rec!.BundleMd);
        Assert.Contains("graph-add", r.WillEnter); // enters the human gate, not map/
        Assert.DoesNotContain("map/", rec.BundleMd);
    }

    [Fact]
    public async Task Capture_is_idempotent_on_the_same_fact()
    {
        var store = new InMemoryDepositStore();
        var svc = new CaptureService(store);
        var a = await svc.CaptureAsync("same fact", null, Array.Empty<string>(), confirm: true, Today);
        var b = await svc.CaptureAsync("same fact", null, Array.Empty<string>(), confirm: true, Today);
        Assert.Equal(a.DepositId, b.DepositId); // deterministic id → a re-capture is the same deposit
    }

    // ── goal write (reusing the host fakes' graph-writer wiring) ─────────────────────────────────

    private static (GoalService svc, FakeMapGraph graph, CapturingPrWriter pr) BuildGoal()
    {
        var graph = new FakeMapGraph();
        var pr = new CapturingPrWriter();
        var writer = new CervelloGraphWriter(pr, new AlwaysResolveLinks(), new FixedPinStore());
        return (new GoalService(graph, writer), graph, pr);
    }

    [Fact]
    public async Task Set_goal_preview_renders_type_goal_with_movimento_and_no_pr()
    {
        var (svc, _, pr) = BuildGoal();
        var r = await svc.SetGoalAsync(new SetGoalRequest("Raise Series A", Status: "active", Objective: "close the round"), Today, default);

        Assert.Equal("preview", r.Status);
        Assert.Null(r.PrBranch);
        Assert.Contains("type: goal", r.Preview);
        Assert.Contains("## Movimento", r.Preview);
        Assert.Contains("## Obiettivo", r.Preview);
        Assert.Equal("map/goals/raise-series-a.md", r.Path);
        Assert.Null(pr.Last); // confirm-by-default: no PR opened
    }

    [Fact]
    public async Task Set_goal_rejects_an_invalid_status()
    {
        var (svc, _, _) = BuildGoal();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SetGoalAsync(new SetGoalRequest("g", Status: "frozen"), Today, default));
    }

    [Fact]
    public async Task Set_goal_confirm_opens_a_review_pr()
    {
        var (svc, _, pr) = BuildGoal();
        var r = await svc.SetGoalAsync(new SetGoalRequest("New Goal", Confirm: true), Today, default);
        Assert.Equal("created", r.Status);
        Assert.NotNull(pr.Last);
        Assert.Contains(pr.Last!.Mutations, m => m.DossierPath == "map/goals/new-goal.md");
    }

    [Fact]
    public async Task Set_goal_update_preserves_prior_movimento_lines()
    {
        var (svc, graph, _) = BuildGoal();
        graph.Objects["Goal:existing"] = new(MapObjectKind.Goal, "existing",
            new Dictionary<string, string> { ["name"] = "Existing", ["status"] = "active" }, "## Stato\nx", new[] { "map/goals/existing.md" });
        graph.Timelines["goal:existing"] = new() { new("2026-05-01", "prior move", new[] { "existing" }, "rec://prior") };

        var r = await svc.SetGoalAsync(new SetGoalRequest("Existing", Status: "stalled"), Today, default);
        Assert.Contains("prior move", r.Preview);       // the prior grounded line survives the update (INGEST §5)
        Assert.Contains("rec://prior", r.Preview);
    }

    [Fact]
    public async Task Link_evidence_requires_a_resolvable_source_ref()
    {
        var (svc, graph, _) = BuildGoal();
        graph.Objects["Goal:g"] = new(MapObjectKind.Goal, "g", new Dictionary<string, string> { ["name"] = "G", ["status"] = "active" }, "", new[] { "map/goals/g.md" });
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.LinkEvidenceAsync(new LinkEvidenceRequest("g", "not a ref", "some fact"), Today, default));
    }

    [Fact]
    public async Task Link_evidence_appends_a_sourced_movimento_line_via_pr()
    {
        var (svc, graph, pr) = BuildGoal();
        graph.Objects["Goal:g"] = new(MapObjectKind.Goal, "g", new Dictionary<string, string> { ["name"] = "G", ["status"] = "active" }, "", new[] { "map/goals/g.md" });

        var r = await svc.LinkEvidenceAsync(new LinkEvidenceRequest("g", "rec://call-1#s3", "raised valuation", "2026-07-02", Confirm: true), Today, default);
        Assert.Equal("linked", r.Status);
        Assert.Contains("raised valuation", r.Line);
        Assert.Contains("rec://call-1#s3", r.Line);
        Assert.NotNull(pr.Last);
    }

    [Fact]
    public async Task Link_evidence_external_ref_is_pinned_on_cite()
    {
        var (svc, graph, pr) = BuildGoal();
        graph.Objects["Goal:g"] = new(MapObjectKind.Goal, "g", new Dictionary<string, string> { ["name"] = "G", ["status"] = "active" }, "", new[] { "map/goals/g.md" });

        await svc.LinkEvidenceAsync(new LinkEvidenceRequest("g", "drive://FILEID", "doc says X", Confirm: true), Today, default);
        // The graph-writer pins external refs; the merged mutation cites pin:// with the drive ref as provenance.
        var mutation = Assert.Single(pr.Last!.Mutations);
        Assert.StartsWith("pin://", mutation.Source);
        Assert.Contains("drive://FILEID", mutation.Source);
    }

    [Fact]
    public async Task Link_evidence_unknown_goal_reports_unknown()
    {
        var (svc, _, _) = BuildGoal();
        var r = await svc.LinkEvidenceAsync(new LinkEvidenceRequest("nope", "rec://x", "fact", Confirm: true), Today, default);
        Assert.Equal("unknown_goal", r.Status);
    }

    // ── local fakes ─────────────────────────────────────────────────────────────────────────────

    private sealed class FakeMapGraph : IMapGraph
    {
        public Dictionary<string, MapObject> Objects { get; } = new();
        public Dictionary<string, List<TimelineLine>> Timelines { get; } = new();
        public Task<MapObject?> GetObjectAsync(MapObjectKind kind, string slug, CancellationToken ct = default) =>
            Task.FromResult(Objects.TryGetValue($"{kind}:{slug}", out var o) ? o : null);
        public Task<IReadOnlyList<TimelineLine>> WalkTimelineAsync(string anchor, string? from, string? to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TimelineLine>>(Timelines.TryGetValue(anchor, out var l) ? l : new List<TimelineLine>());
        public Task<IReadOnlyList<GraphNeighbour>> NeighboursAsync(MapObjectKind kind, string slug, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GraphNeighbour>>(Array.Empty<GraphNeighbour>());
        public Task<IReadOnlyList<string>> ListGoalSlugsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Objects.Keys.Where(k => k.StartsWith("Goal:")).Select(k => k[5..]).ToList());
    }

    private sealed class CapturingPrWriter : IMapPrWriter
    {
        public MapReviewPr? Last { get; private set; }
        public Task<MapPrHandle> OpenPrAsync(MapReviewPr pr, CancellationToken ct = default)
        {
            Last = pr;
            return Task.FromResult(new MapPrHandle(pr.Branch, pr.Title));
        }
    }

    private sealed class AlwaysResolveLinks : ILinkResolver
    {
        public Task<bool> DossierExistsAsync(string slug, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FixedPinStore : IPinStore
    {
        public Task<string> PinAsync(string externalRef, CancellationToken ct = default) => Task.FromResult("cafebabe");
    }
}
