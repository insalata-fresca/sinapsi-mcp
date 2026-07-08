using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline.Stages;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The apply stage / GRAPH-ADD (spec <c>enrichment-linking</c> → "Apply via GRAPH-ADD under the
/// decision policy"; DESIGN §5; lint R1/R4/R5/R11). Proves auto facts open ONE back-linked map PR
/// with source + basis, ambiguous facts go to the open-points queue (never the map), a missing
/// dossier is declared as a stub, and external evidence is pinned. All against fakes.
/// </summary>
public sealed class ApplyStageTests
{
    private const string Bundle = "2026-07-01-standup";
    private const string Rec = "rec-2026-07-01-standup";

    private static (ApplyStage stage, FakeMapPrWriter pr, InMemoryOpenPointStore points) Build(
        params string[] existingDossiers)
    {
        var pr = new FakeMapPrWriter();
        var points = new InMemoryOpenPointStore();
        var writer = new CervelloGraphWriter(pr, new FakeLinkResolver(existingDossiers), new FakePinStore());
        return (new ApplyStage(writer, points), pr, points);
    }

    private static ApplyInput Input(
        IReadOnlyList<AttributionVerdict>? attributions = null,
        IReadOnlyList<CorrectionVerdict>? corrections = null,
        IReadOnlyList<MapMutation>? timeline = null,
        IReadOnlyList<ReferencedLink>? links = null) =>
        new(Bundle, Rec, "2026-07-01",
            attributions ?? Array.Empty<AttributionVerdict>(),
            corrections ?? Array.Empty<CorrectionVerdict>(),
            timeline ?? Array.Empty<MapMutation>(),
            links ?? Array.Empty<ReferencedLink>());

    // ── Scenario: Auto facts open a back-linked map PR (R1/R5) ──────────────────────────────────
    [Fact]
    public async Task Auto_facts_open_a_back_linked_map_pr_with_source_and_basis()
    {
        var (stage, pr, _) = Build("guilhem");
        var applied = AttributionVerdict.AutoApplied("s1", "guilhem", 0.83, $"rec://{Rec}#s1", ConfirmationBasis.Auto("v1"));
        var timeline = new MapMutation(
            "map/timeline.md", "## Timeline",
            $"- 2026-07-01 — Q3 numbers reviewed — source: rec://{Rec}#s1 [[guilhem]]",
            $"rec://{Rec}#s1", 0.9, Bundle);

        var result = await stage.ApplyAsync(Input(attributions: [applied], timeline: [timeline], links: [new ReferencedLink("guilhem", "person")]));

        Assert.NotNull(result.Pr);
        var opened = pr.LastPr!;
        // R5: every mutation + the body back-links the bundle.
        var body = opened.RenderBody();
        Assert.Contains($"bundle://{Bundle}", body);
        Assert.All(opened.Mutations, m => Assert.Equal(Bundle, m.BundleId));
        // R1: the timeline mutation carries a source: ref.
        Assert.Contains(opened.Mutations, m => m.Content.Contains("source:", StringComparison.Ordinal));
        // R9: the attribution mutation carries the auto basis id.
        Assert.Contains(opened.Mutations, m => m.BasisId == "auto://voice-match@v1");
    }

    // ── Scenario: Ambiguous facts do not enter the map ──────────────────────────────────────────
    [Fact]
    public async Task Ambiguous_attribution_goes_to_open_points_never_the_map()
    {
        var (stage, pr, points) = Build();
        var escalated = AttributionVerdict.OpenPoint("s1", 0.7, "two enrolled people both in the auto band");

        var result = await stage.ApplyAsync(Input(attributions: [escalated]));

        Assert.Null(result.Pr);               // no map PR opened
        Assert.Equal(0, pr.Opened);
        Assert.Equal(1, result.OpenPoints);
        var pending = await points.ListPendingAsync(Rec);
        var pt = Assert.Single(pending);
        Assert.Equal(OpenPointKind.Speaker, pt.Kind);
        Assert.Equal($"bundle://{Bundle}", pt.BundleRef); // back-linked (R5 posture for queue)
    }

    // ── Scenario: Proposed link to a missing dossier becomes a stub (R4) ────────────────────────
    [Fact]
    public async Task Auto_link_to_missing_dossier_declares_a_stub_R4()
    {
        // The [[acme-deal]] project dossier does NOT exist → the same PR declares it a stub.
        var (stage, pr, _) = Build(/* only guilhem exists */ "guilhem");
        var timeline = new MapMutation(
            "map/people/guilhem.md", "## Timeline",
            $"- 2026-07-01 — mentioned the acme deal — source: rec://{Rec}#s1 [[acme-deal]]",
            $"rec://{Rec}#s1", 0.8, Bundle);

        var result = await stage.ApplyAsync(Input(
            timeline: [timeline],
            links: [new ReferencedLink("guilhem", "person"), new ReferencedLink("acme-deal", "project")]));

        Assert.NotNull(result.Pr);
        var stub = Assert.Single(pr.LastPr!.Stubs);
        Assert.Equal("acme-deal", stub.Slug);
        Assert.Equal("map/projects/acme-deal.md", stub.Path);
        Assert.Contains("stub: true", stub.RenderStub("2026-07-01"));
    }

    // ── R11: external evidence in a merged map line is pinned ────────────────────────────────────
    [Fact]
    public async Task External_evidence_is_pinned_R11()
    {
        var (stage, pr, _) = Build("guilhem");
        var driveRef = "drive://1AbCdEf";
        var timeline = new MapMutation(
            "map/people/guilhem.md", "## Timeline",
            $"- 2026-07-01 — signed the mandate — source: {driveRef}",
            driveRef, 0.8, Bundle);

        var result = await stage.ApplyAsync(Input(timeline: [timeline], links: [new ReferencedLink("guilhem", "person")]));

        Assert.NotNull(result.Pr);
        var m = Assert.Single(pr.LastPr!.Mutations);
        // The merged line now cites pin://<sha> with the external ref as provenance only.
        Assert.StartsWith("pin://", m.Source);
        Assert.Contains("(drive://1AbCdEf)", m.Source);
        Assert.Contains("pin://", m.Content);
        Assert.DoesNotContain("source: drive://", m.Content); // no bare external ref survives
    }

    // ── Scenario: omitted facts are dropped, gap recorded ───────────────────────────────────────
    [Fact]
    public async Task Omitted_attribution_is_dropped_and_recorded_never_written()
    {
        var (stage, pr, points) = Build();
        var omitted = AttributionVerdict.Omitted("s2", "below reject band, no prior → unidentified");

        var result = await stage.ApplyAsync(Input(attributions: [omitted]));

        Assert.Null(result.Pr);
        Assert.Equal(1, result.Omitted);
        Assert.Empty(await points.ListPendingAsync()); // not escalated either — just dropped
    }

    // ── Escalate-only end-to-end: applied verdicts are already downgraded → no PR, all queued ────
    [Fact]
    public async Task Escalate_only_verdicts_open_no_pr_and_fill_the_queue()
    {
        var (stage, pr, points) = Build();
        // In escalate-only the policy produces OpenPoint verdicts even for strong matches.
        var gated = AttributionVerdict.OpenPoint("s1", 0.9, "escalate-only phase: withholding auto-apply");

        var result = await stage.ApplyAsync(Input(attributions: [gated]));

        Assert.Null(result.Pr);
        Assert.Equal(0, pr.Opened);
        Assert.Single(await points.ListPendingAsync());
    }
}
