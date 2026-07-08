using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// End-to-end wiring of the attribution stage (spec <c>speaker-attribution</c> → the decision
/// policy + basis; DESIGN §5.2 step 2): merged cluster → prior + voiceprint match → decision
/// policy → verdict with basis. Uses the in-memory store + a fake prior; synthetic vectors only.
/// </summary>
public sealed class AttributionStageTests
{
    private const string Rec = "rec-guilhem-121";
    private static readonly DateOnly Day = new(2026, 7, 1);

    private static MergedCluster Cluster(string speaker, float[] centroid) =>
        new(speaker, [speaker], centroid, [new DiarizedSegment(speaker, 0, 5)]);

    private static IPriorSource Prior(string recordingId, PriorCandidates candidates) =>
        new FilenameParticipantPriorSource(new Dictionary<string, PriorCandidates> { [recordingId] = candidates });

    private static AttributionStage Stage(
        IVoiceprintStore store, IPriorSource prior, PolicyPhase phase) =>
        new(store, prior, new DecisionPolicy(DecisionBands.Default, phase));

    private static async Task<InMemoryVoiceprintStore> EnrolledStore(params (string slug, float[] vec)[] people)
    {
        var store = new InMemoryVoiceprintStore(new EnrollmentAllowlist(people.Select(p => p.slug)));
        var e = new VoiceprintEnrollment(store);
        foreach (var (slug, vec) in people)
            await e.EnrollOnConfirmationAsync(slug, vec, [$"rec://seed#{slug}"], null,
                ConfirmationBasis.Human($"seed-{slug}"), Day);
        return store;
    }

    [Fact] // P5: clean high match + agreeing filename prior → auto-applied with rec:// source + auto basis
    public async Task Clean_match_with_agreeing_prior_auto_applies_under_p5()
    {
        var store = await EnrolledStore(("guilhem", TestVectors.Axis(0)));
        var prior = Prior(Rec, new PriorCandidates(["guilhem"], isStrong: true));
        var stage = Stage(store, prior, PolicyPhase.GradedAutoApply);

        var verdicts = await stage.ResolveAsync(Rec,
            [Cluster("s1", TestVectors.TiltedFromAxis(0, 5, 0.9))]);

        var v = Assert.Single(verdicts);
        Assert.Equal(AttributionOutcome.AutoApplied, v.Outcome);
        Assert.Equal("guilhem", v.Person);
        Assert.Equal($"rec://{Rec}#s1", v.SourceRef);
        Assert.Equal("auto://voice-match@v1", v.Basis!.Id);
    }

    [Fact] // the phase-gate proof, through the real stage: 0.90 clean match ESCALATES while escalate-only
    public async Task Escalate_only_phase_withholds_a_clean_090_match_through_the_stage()
    {
        var store = await EnrolledStore(("guilhem", TestVectors.Axis(0)));
        var prior = Prior(Rec, new PriorCandidates(["guilhem"], isStrong: true));
        var stage = Stage(store, prior, PolicyPhase.EscalateOnly);

        var v = Assert.Single(await stage.ResolveAsync(Rec, [Cluster("s1", TestVectors.TiltedFromAxis(0, 5, 0.90))]));
        Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome);
        Assert.Null(v.Basis);
    }

    [Fact] // conflict: voice matches A but a strong prior names B → escalate, never resolve to A
    public async Task High_voice_match_conflicting_strong_prior_escalates()
    {
        var store = await EnrolledStore(("person-a", TestVectors.Axis(0)));
        var prior = Prior(Rec, new PriorCandidates(["person-b"], isStrong: true));
        var stage = Stage(store, prior, PolicyPhase.GradedAutoApply);

        var v = Assert.Single(await stage.ResolveAsync(Rec, [Cluster("s1", TestVectors.TiltedFromAxis(0, 5, 0.85))]));
        Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome);
        Assert.Null(v.Person);
    }

    [Fact] // below-reject + no prior → omit, never guessed (the no-fabrication floor), even at P5
    public async Task Unknown_speaker_below_reject_is_omitted_never_guessed()
    {
        var store = await EnrolledStore(("guilhem", TestVectors.Axis(0)));
        var prior = Prior(Rec, PriorCandidates.None);
        var stage = Stage(store, prior, PolicyPhase.GradedAutoApply);

        // A cluster orthogonal to the only enrolled print → cosine ~0 → below reject.
        var v = Assert.Single(await stage.ResolveAsync(Rec, [Cluster("s1", TestVectors.Axis(50))]));
        Assert.Equal(AttributionOutcome.Omitted, v.Outcome);
        Assert.Null(v.Person);
    }

    [Fact] // lint R8: a DELETED person is dropped from the match set — no re-attribution on a voice basis
    public async Task No_reattribution_to_a_deleted_person()
    {
        var store = await EnrolledStore(("guilhem", TestVectors.Axis(0)));
        await new VoiceprintEnrollment(store).DeleteVoiceprintAsync("guilhem", Day);

        var prior = Prior(Rec, new PriorCandidates(["guilhem"], isStrong: true));
        var stage = Stage(store, prior, PolicyPhase.GradedAutoApply);

        // Even a perfect centroid match to guilhem must NOT be attributed (tombstoned).
        var v = Assert.Single(await stage.ResolveAsync(Rec, [Cluster("s1", TestVectors.Axis(0))]));
        Assert.NotEqual(AttributionOutcome.AutoApplied, v.Outcome);
        Assert.Equal(AttributionOutcome.Omitted, v.Outcome); // no live match + prior can't resolve alone
        Assert.Null(v.Person);
    }

    [Fact] // two enrolled people both ≥ auto for the same cluster → ambiguous → escalate
    public async Task Two_enrolled_in_auto_band_escalate_through_stage()
    {
        // Two nearly-identical enrolled prints; a cluster close to both lands both ≥ auto.
        var store = await EnrolledStore(
            ("person-a", TestVectors.TiltedFromAxis(0, 1, 0.99)),
            ("person-b", TestVectors.TiltedFromAxis(0, 2, 0.99)));
        var stage = Stage(store, Prior(Rec, PriorCandidates.None), PolicyPhase.GradedAutoApply);

        var v = Assert.Single(await stage.ResolveAsync(Rec, [Cluster("s1", TestVectors.Axis(0))]));
        Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome);
        Assert.Contains("ambiguous", v.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
