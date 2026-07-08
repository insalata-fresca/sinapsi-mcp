using System.Text.Json;
using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Bundles;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline.Stages;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The enrich+link stage + bundle writer (spec <c>enrichment-linking</c> → "Produce the
/// enrichment bundle"; SCHEMAS §6; lint R1/R6/R7). Proves the bundle carries PROPOSED facts only
/// (attribution always <c>needs_confirmation</c>/<c>basis:null</c>), every timeline line has a
/// <c>source:</c> ref, the bundle validates against SCHEMAS §6, and it contains no biometrics.
/// </summary>
public sealed class EnrichBundleStageTests
{
    private const string Id = "2026-07-01-standup";
    private const string Rec = "rec-2026-07-01-standup";

    private static EnrichLinkInput Input(
        IReadOnlyList<ProposedLink>? links = null,
        IReadOnlyList<ProposedTimelineLine>? timeline = null,
        IReadOnlyList<AttributionVerdict>? attribution = null) =>
        new(
            bundleId: Id,
            sourceRef: $"rec://{Rec}",
            idempotencyKey: $"rec:{Rec}:sha",
            kind: "recording",
            createdAt: "2026-07-01T10:00:00Z",
            summary: "Stand-up: Q3 numbers, filing owner assigned.",
            entities: ["Q3", "filing"],
            dates: ["2026-07-01"],
            proposedLinks: links ?? Array.Empty<ProposedLink>(),
            proposedTimeline: timeline ?? Array.Empty<ProposedTimelineLine>(),
            attribution: attribution ?? Array.Empty<AttributionVerdict>(),
            attention: BundleAttention.Promote(0.8, "commercialista thread"));

    // ── Scenario: Bundle carries proposed facts, not applied ones ───────────────────────────────
    [Fact]
    public async Task Bundle_attribution_is_always_needs_confirmation_with_null_basis()
    {
        // An applied verdict from the attribution stage is projected DOWN to needs_confirmation.
        var applied = AttributionVerdict.AutoApplied("s1", "guilhem", 0.83, "rec://r#s1", ConfirmationBasis.Auto("v1"));
        var stage = new EnrichLinkStage(new FakeLinkResolver("guilhem"));

        var bundle = await stage.EnrichAsync(Input(attribution: [applied]));

        var entry = Assert.Single(bundle.Enrichment.Attribution);
        Assert.Equal("needs_confirmation", entry.Status);
        Assert.Null(entry.Basis);
        Assert.Equal("guilhem", entry.Candidate);

        // And that survives the SCHEMAS §6 serialization: status present, basis null.
        var json = BundleWriter.RenderDataJson(bundle);
        using var doc = JsonDocument.Parse(json);
        var attr = doc.RootElement.GetProperty("enrichment").GetProperty("attribution")[0];
        Assert.Equal("needs_confirmation", attr.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, attr.GetProperty("basis").ValueKind);
    }

    [Fact]
    public async Task An_omitted_speaker_is_not_proposed_as_an_attribution_never_guess()
    {
        var omitted = AttributionVerdict.Omitted("s2", "below reject band, no prior → unidentified");
        var stage = new EnrichLinkStage(new FakeLinkResolver());

        var bundle = await stage.EnrichAsync(Input(attribution: [omitted]));

        Assert.Empty(bundle.Enrichment.Attribution); // no phantom name for an unidentified speaker
    }

    // ── Scenario: every timeline line carries a source ref (R1) ─────────────────────────────────
    [Fact]
    public void A_timeline_line_without_a_source_cannot_be_constructed_R1()
    {
        Assert.Throws<ArgumentException>(() => new ProposedTimelineLine("2026-07-01", "the fact", source: ""));
        var ok = new ProposedTimelineLine("2026-07-01", "Q3 numbers reviewed", "rec://r#s1");
        Assert.Equal("- 2026-07-01 — Q3 numbers reviewed — source: rec://r#s1", ok.ToTimelineLine());
    }

    [Fact]
    public async Task Bundle_validates_against_schemas_section_6()
    {
        var stage = new EnrichLinkStage(new FakeLinkResolver("guilhem", "acme-deal"));
        var bundle = await stage.EnrichAsync(Input(
            links: [new ProposedLink("[[guilhem]]", 0.7), new ProposedLink("[[acme-deal]]", 0.6)],
            timeline: [new ProposedTimelineLine("2026-07-01", "filing owner assigned", "rec://r#s1")],
            attribution: [AttributionVerdict.AutoApplied("s1", "guilhem", 0.83, "rec://r#s1", ConfirmationBasis.Auto("v1"))]));

        var json = BundleWriter.RenderDataJson(bundle);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Top-level SCHEMAS §6 fields.
        Assert.Equal(Id, root.GetProperty("bundle_id").GetString());
        Assert.Equal($"rec://{Rec}", root.GetProperty("source_ref").GetString());
        Assert.Equal("recording", root.GetProperty("kind").GetString());
        Assert.Equal("bundle_created", root.GetProperty("state").GetString());

        var enr = root.GetProperty("enrichment");
        Assert.True(enr.GetProperty("summary").GetString()!.Length > 0);
        Assert.Equal("[[guilhem]]", enr.GetProperty("proposed_links")[0].GetProperty("target").GetString());
        var tl = enr.GetProperty("proposed_timeline")[0];
        Assert.Equal("rec://r#s1", tl.GetProperty("source").GetString());

        var att = root.GetProperty("attention");
        Assert.Equal("promote", att.GetProperty("verdict").GetString());
    }

    // ── Scenario: Bundle contains no biometrics (R7) ────────────────────────────────────────────
    [Fact]
    public async Task Bundle_contains_no_biometrics_and_write_self_checks_R7()
    {
        var stage = new EnrichLinkStage(new FakeLinkResolver("guilhem"));
        var bundle = await stage.EnrichAsync(Input(
            attribution: [AttributionVerdict.AutoApplied("s1", "guilhem", 0.83, "rec://r#s1", ConfirmationBasis.Auto("v1"))]));

        var json = BundleWriter.RenderDataJson(bundle);
        Assert.DoesNotContain("vector", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("centroid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("embedding", json, StringComparison.OrdinalIgnoreCase);

        // The writer's R7 self-check passes for a clean bundle and the store gets the pair.
        var store = new InMemoryBundleStore();
        var res = await new BundleWriter(store).WriteAsync(bundle);
        Assert.False(res.AlreadyExisted);
        var stored = store.Read(Id);
        Assert.NotNull(stored);
        Assert.Contains($"bundle://{Id}", stored!.Value.BundleMd); // R5 back-link marker
    }

    [Fact]
    public async Task Bundle_write_is_idempotent()
    {
        var stage = new EnrichLinkStage(new FakeLinkResolver());
        var bundle = await stage.EnrichAsync(Input());
        var store = new InMemoryBundleStore();
        var writer = new BundleWriter(store);

        var first = await writer.WriteAsync(bundle);
        var second = await writer.WriteAsync(bundle);
        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
    }

    // ── R7 guard actively blocks a base64 blob ──────────────────────────────────────────────────
    [Fact]
    public void BundleGuard_blocks_a_large_base64_blob_R7()
    {
        var blob = new string('A', 11 * 1024); // > 10 KiB base64 run
        Assert.Throws<InvalidOperationException>(() => BundleGuard.EnsureNoBinaries($"data: {blob}"));
        BundleGuard.EnsureNoBinaries("- 2026-07-01 — fact — source: rec://r#s1"); // clean line ok
    }
}
