using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Cervello.Enrichment.Pipeline.Stages;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// V6 corpus re-attribution tests (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V6, §9 forks 2 &amp; 3): a just-enrolled print
/// requeues ONLY the recordings that actually match; auto-band matches carry the recent-enrollment
/// auto-apply signal; borderline (below-auto) matches still requeue-to-escalate but never mark
/// auto-apply; non-matching recordings are never touched. Then the AttributionStage auto-applies for
/// the just-enrolled print at ≥ auto, and STILL escalates a below-auto match. SYNTHETIC vectors only.
/// </summary>
public sealed class CorpusReattributorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
    private const string Basis = "human://rename:file-3";

    private static RecordingVoiceprint Row(string rec, int idx, float[] centroid) =>
        new(rec, idx, centroid, "pyannote/wespeaker-voxceleb-resnet34-LM", 1, 20.0, "s1", T0, [new DiarizedSegment("s1", 0, 20)]);

    [Fact] // scenario: only the recording whose centroid matches ≥ auto is requeued + marked for auto-apply
    public async Task Auto_band_match_requeues_and_marks_recent_enrollment()
    {
        var corpus = new InMemoryRecordingVoiceprintStore();
        var newPrint = TestVectors.Axis(5);
        await corpus.PersistAsync("rec-match", [Row("rec-match", 0, TestVectors.Axis(5))]);   // cosine 1.0 → auto
        await corpus.PersistAsync("rec-other", [Row("rec-other", 0, TestVectors.Axis(50))]);  // cosine 0.0 → reject

        var requeue = new InMemoryRecordingRequeue();
        var recent = new InMemoryRecentEnrollmentStore();
        var reattr = new CorpusReattributor(corpus, requeue, recent, DecisionBands.Default);

        var result = await reattr.ReattributeAsync("marco", newPrint, Basis);

        Assert.Equal(["rec-match"], result.AutoBandRecordingIds);
        Assert.Empty(result.BorderlineRecordingIds);
        Assert.Equal(["rec-match"], result.RequeuedRecordingIds);
        Assert.DoesNotContain("rec-other", requeue.Requeued);           // non-matching NEVER touched
        Assert.Equal(Basis, await recent.GetBasisAsync("marco"));       // auto-apply authorised for marco
    }

    [Fact] // scenario: a borderline (review-band) match is requeued to ESCALATE but never marks auto-apply
    public async Task Borderline_match_requeues_but_does_not_mark_auto_apply()
    {
        var corpus = new InMemoryRecordingVoiceprintStore();
        var newPrint = TestVectors.Axis(0);
        // cosine 0.55 sits in [reject 0.45, auto 0.62) → borderline.
        await corpus.PersistAsync("rec-borderline", [Row("rec-borderline", 0, TestVectors.TiltedFromAxis(0, 1, 0.55))]);

        var requeue = new InMemoryRecordingRequeue();
        var recent = new InMemoryRecentEnrollmentStore();
        var reattr = new CorpusReattributor(corpus, requeue, recent, DecisionBands.Default);

        var result = await reattr.ReattributeAsync("marco", newPrint, Basis);

        Assert.Empty(result.AutoBandRecordingIds);
        Assert.Equal(["rec-borderline"], result.BorderlineRecordingIds);
        Assert.Equal(["rec-borderline"], result.RequeuedRecordingIds);
        Assert.Null(await recent.GetBasisAsync("marco")); // NOT marked — borderline must escalate, not auto-apply
    }

    [Fact] // scenario: a below-reject recording is never requeued (only matches are reset)
    public async Task Non_matching_recording_is_never_requeued()
    {
        var corpus = new InMemoryRecordingVoiceprintStore();
        await corpus.PersistAsync("rec-nomatch", [Row("rec-nomatch", 0, TestVectors.Axis(80))]);

        var requeue = new InMemoryRecordingRequeue();
        var recent = new InMemoryRecentEnrollmentStore();
        var reattr = new CorpusReattributor(corpus, requeue, recent, DecisionBands.Default);

        var result = await reattr.ReattributeAsync("marco", TestVectors.Axis(0), Basis);

        Assert.Equal(0, result.MatchedCount);
        Assert.Empty(requeue.Requeued);
        Assert.Null(await recent.GetBasisAsync("marco"));
    }

    // ── the drain-side half: AttributionStage auto-applies a just-enrolled print at ≥ auto, escalates below ──

    [Fact] // scenario: after V6 marks marco, the AttributionStage AUTO-APPLIES an auto-band match with human basis
    public async Task AttributionStage_auto_applies_just_enrolled_print_at_auto_band()
    {
        var consent = new InMemoryEnrollmentConsentStore();
        var store = new InMemoryVoiceprintStore(EnrollmentAllowlist.Empty, consent);
        await consent.AddConsentAsync("marco", Basis);
        await store.EnrollOrRefineAsync("marco", TestVectors.Axis(5), ["rec://rec-match#s1"], null, new DateOnly(2026, 7, 10));

        var recent = new InMemoryRecentEnrollmentStore();
        await recent.MarkAsync("marco", Basis);

        // EscalateOnly is the DEFAULT phase — proving the auto-apply bypasses the phase gate for a just-enrolled print.
        var policy = new DecisionPolicy(DecisionBands.Default, PolicyPhase.EscalateOnly);
        var stage = new AttributionStage(store, policy, participantHints: null, recentEnrollment: recent);

        var cluster = new MergedCluster("s1", ["s1"], TestVectors.Axis(5), [new DiarizedSegment("s1", 0, 20)]);
        var result = await stage.ResolveAsync("rec-match", [cluster]);

        var verdict = Assert.Single(result.Verdicts);
        Assert.Equal(AttributionOutcome.AutoApplied, verdict.Outcome);
        Assert.Equal("marco", verdict.Person);
        Assert.Equal(ConfirmationBasisKind.Human, verdict.Basis!.Kind);
        Assert.Equal(Basis, verdict.Basis!.Id);
    }

    [Fact] // scenario: a BORDERLINE match to a just-enrolled print STILL escalates (never mislabels — §9 fork 3)
    public async Task AttributionStage_escalates_borderline_match_to_just_enrolled_print()
    {
        var consent = new InMemoryEnrollmentConsentStore();
        var store = new InMemoryVoiceprintStore(EnrollmentAllowlist.Empty, consent);
        await consent.AddConsentAsync("marco", Basis);
        await store.EnrollOrRefineAsync("marco", TestVectors.Axis(0), ["rec://r#s1"], null, new DateOnly(2026, 7, 10));

        var recent = new InMemoryRecentEnrollmentStore();
        await recent.MarkAsync("marco", Basis);

        var policy = new DecisionPolicy(DecisionBands.Default, PolicyPhase.EscalateOnly);
        var stage = new AttributionStage(store, policy, participantHints: null, recentEnrollment: recent);

        // The cluster cosine to marco's print is 0.55 → borderline (below auto 0.62, above reject 0.45).
        var cluster = new MergedCluster("s1", ["s1"], TestVectors.TiltedFromAxis(0, 1, 0.55), [new DiarizedSegment("s1", 0, 20)]);
        var result = await stage.ResolveAsync("rec-borderline", [cluster]);

        var verdict = Assert.Single(result.Verdicts);
        Assert.Equal(AttributionOutcome.OpenPoint, verdict.Outcome); // escalated, never auto-applied
    }

    // ── the MC write-safety BLOCKER regression: a STALE mark must NOT auto-apply a future match ──────

    [Fact] // scenario: mark(marco) → within TTL auto-applies, but PAST TTL a NEW ≥auto match ESCALATES
    public async Task AttributionStage_escalates_a_future_match_after_the_recent_enrollment_ttl_elapses()
    {
        var consent = new InMemoryEnrollmentConsentStore();
        var store = new InMemoryVoiceprintStore(EnrollmentAllowlist.Empty, consent);
        await consent.AddConsentAsync("marco", Basis);
        await store.EnrollOrRefineAsync("marco", TestVectors.Axis(5), ["rec://r#s1"], null, new DateOnly(2026, 7, 10));

        // A mutable clock: the mark is set at T0; the clock later advances PAST the TTL before the match.
        var clock = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var recent = new InMemoryRecentEnrollmentStore(TimeSpan.FromHours(3), () => clock);
        await recent.MarkAsync("marco", Basis); // marked at T0

        var policy = new DecisionPolicy(DecisionBands.Default, PolicyPhase.EscalateOnly);
        var stage = new AttributionStage(store, policy, participantHints: null, recentEnrollment: recent);
        var cluster = new MergedCluster("s1", ["s1"], TestVectors.Axis(5), [new DiarizedSegment("s1", 0, 20)]); // cosine 1.0 → ≥auto

        // WITHIN the window → the propagation pass DOES auto-apply (the single-pass contract still holds).
        clock = clock.AddHours(2);
        var within = await stage.ResolveAsync("rec-propagation", [cluster]);
        Assert.Equal(AttributionOutcome.AutoApplied, Assert.Single(within.Verdicts).Outcome);

        // PAST the TTL (a brand-new, unrelated recording — here 4 h later, in prod possibly weeks) → ESCALATE.
        clock = clock.AddHours(2); // now T0 + 4h > 3h TTL
        var later = await stage.ResolveAsync("rec-unrelated-future", [cluster]);
        Assert.Equal(AttributionOutcome.OpenPoint, Assert.Single(later.Verdicts).Outcome); // stale mark inert — escalate-only holds
    }

    [Fact] // scenario: the store itself expires a stale mark — GetBasisAsync returns null past the TTL
    public async Task RecentEnrollmentStore_expires_a_stale_mark()
    {
        var clock = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryRecentEnrollmentStore(TimeSpan.FromMinutes(180), () => clock);
        await store.MarkAsync("marco", Basis);

        clock = clock.AddMinutes(179);
        Assert.Equal(Basis, await store.GetBasisAsync("marco")); // still within window

        clock = clock.AddMinutes(2); // now 181 min > 180 min TTL
        Assert.Null(await store.GetBasisAsync("marco"));         // stale → inert, never authorises auto-apply
    }
}
