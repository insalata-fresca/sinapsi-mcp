using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The ENROLL-BASED attribution stage (design: enroll-based attribution + participant hint). Four
/// bounded rules, in order: enrolled voice match → participant-hint 1:1 (+enroll proposal) → local
/// "Unknown speaker N" → ambiguous open-point. Never fabricates a name; never links across recordings;
/// escalate-only apply gate unchanged (M4 dark). Synthetic vectors only.
/// </summary>
public sealed class AttributionStageTests
{
    private const string Rec = "20260704-standup";
    private static readonly DateOnly Day = new(2026, 7, 1);

    private static MergedCluster Cluster(string speaker, float[] centroid) =>
        new(speaker, [speaker], centroid, [new DiarizedSegment(speaker, 0, 5)]);

    private static IParticipantHintSource Hints(params string[] participants) =>
        new InMemoryParticipantHintSource(new Dictionary<string, IReadOnlyList<string>> { [Rec] = participants });

    private static AttributionStage Stage(
        IVoiceprintStore store, PolicyPhase phase, IParticipantHintSource? hints = null) =>
        new(store, new DecisionPolicy(DecisionBands.Default, phase), hints);

    private static async Task<InMemoryVoiceprintStore> EnrolledStore(params (string slug, float[] vec)[] people)
    {
        var store = new InMemoryVoiceprintStore(new EnrollmentAllowlist(people.Select(p => p.slug)));
        var e = new VoiceprintEnrollment(store);
        foreach (var (slug, vec) in people)
            await e.EnrollOnConfirmationAsync(slug, vec, [$"rec://seed#{slug}"], null,
                ConfirmationBasis.Human($"seed-{slug}"), Day);
        return store;
    }

    // ── 1. ENROLLED MATCH ───────────────────────────────────────────────────────────────────────────

    [Fact] // an ENROLLED voice ≥ auto band → auto-attributed (under GradedAutoApply)
    public async Task An_enrolled_voice_is_auto_attributed_under_graded_auto_apply()
    {
        var store = await EnrolledStore(("guilhem", TestVectors.Axis(0)));
        var stage = Stage(store, PolicyPhase.GradedAutoApply);

        var result = await stage.ResolveAsync(Rec, [Cluster("s1", TestVectors.TiltedFromAxis(0, 5, 0.9))]);
        var v = Assert.Single(result.Verdicts);
        Assert.Equal(AttributionOutcome.AutoApplied, v.Outcome);
        Assert.Equal("guilhem", v.Person);
        Assert.Equal($"rec://{Rec}#s1", v.SourceRef);
        Assert.Empty(result.EnrollmentProposals); // an enrolled match needs no enroll proposal
    }

    [Fact] // the escalate-only gate is UNCHANGED — a clean 0.90 enrolled match still escalates in M4
    public async Task Escalate_only_withholds_a_clean_enrolled_match()
    {
        var store = await EnrolledStore(("guilhem", TestVectors.Axis(0)));
        var stage = Stage(store, PolicyPhase.EscalateOnly);

        var v = Assert.Single((await stage.ResolveAsync(Rec, [Cluster("s1", TestVectors.TiltedFromAxis(0, 5, 0.90))])).Verdicts);
        Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome);
        Assert.Null(v.Basis);
    }

    [Fact] // lint R8: a DELETED person is never re-attributed on a voice basis
    public async Task No_reattribution_to_a_deleted_person()
    {
        var store = await EnrolledStore(("guilhem", TestVectors.Axis(0)));
        await new VoiceprintEnrollment(store).DeleteVoiceprintAsync("guilhem", Day);
        var stage = Stage(store, PolicyPhase.GradedAutoApply);

        // A perfect centroid match to guilhem must NOT auto-apply (tombstoned) → local unknown (no hint).
        var v = Assert.Single((await stage.ResolveAsync(Rec, [Cluster("s1", TestVectors.Axis(0))])).Verdicts);
        Assert.NotEqual(AttributionOutcome.AutoApplied, v.Outcome);
        Assert.Null(v.Person);
        Assert.Equal("Unknown speaker 1", v.LocalUnknownLabel);
    }

    [Fact] // two enrolled people both ≥ auto for one cluster → ambiguous → escalate (unchanged)
    public async Task Two_enrolled_in_auto_band_escalate()
    {
        var store = await EnrolledStore(
            ("person-a", TestVectors.TiltedFromAxis(0, 1, 0.99)),
            ("person-b", TestVectors.TiltedFromAxis(0, 2, 0.99)));
        var stage = Stage(store, PolicyPhase.GradedAutoApply);

        var v = Assert.Single((await stage.ResolveAsync(Rec, [Cluster("s1", TestVectors.Axis(0))])).Verdicts);
        Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome);
        Assert.Contains("ambiguous", v.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── 2. PARTICIPANT-HINT ASSIGNMENT (1:1) ─────────────────────────────────────────────────────────

    [Fact] // a named-but-UNENROLLED participant, unambiguous 1:1 → attributed + enrollment PROPOSED,
           //  but NOT written under EscalateOnly (open-point; the enroll write stays behind the gate)
    public async Task A_1to1_participant_hint_proposes_enrollment_but_does_not_write_under_escalate_only()
    {
        var store = await EnrolledStore(); // nobody enrolled
        var stage = Stage(store, PolicyPhase.EscalateOnly, Hints("marco"));

        var voice = TestVectors.Axis(30); // unenrolled voice
        var result = await stage.ResolveAsync(Rec, [Cluster("s1", voice)]);

        // The single unmatched voice + the single unaccounted participant → a proposal is produced,
        // but under EscalateOnly the verdict is an OPEN-POINT (not auto-applied — no write).
        var v = Assert.Single(result.Verdicts);
        Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome);
        Assert.Contains("marco", v.Reason, StringComparison.OrdinalIgnoreCase);
        var proposal = Assert.Single(result.EnrollmentProposals);
        Assert.Equal("marco", proposal.PersonSlug);
        Assert.Equal($"rec://{Rec}#s1", proposal.SourceRef);
        Assert.Equal(voice, proposal.Centroid);
    }

    [Fact] // under a SIMULATED GradedAutoApply the same 1:1 hint AUTO-APPLIES + proposes enrollment
    public async Task A_1to1_participant_hint_auto_applies_and_proposes_enrollment_under_graded_auto_apply()
    {
        var store = await EnrolledStore();
        var stage = Stage(store, PolicyPhase.GradedAutoApply, Hints("marco"));

        var result = await stage.ResolveAsync(Rec, [Cluster("s1", TestVectors.Axis(30))]);
        var v = Assert.Single(result.Verdicts);
        Assert.Equal(AttributionOutcome.AutoApplied, v.Outcome);
        Assert.Equal("marco", v.Person);
        Assert.StartsWith("auto://participant-hint", v.Basis!.Id); // a metadata basis, not a voice match
        Assert.Single(result.EnrollmentProposals);
    }

    [Fact] // a participant already accounted for by an ENROLLED match is not double-assigned by the hint
    public async Task A_hint_participant_already_enrolled_does_not_double_assign()
    {
        var store = await EnrolledStore(("guilhem", TestVectors.Axis(0)));
        // Two voices: guilhem (enrolled) + an unknown; hints name guilhem + marco. guilhem is accounted
        // for by the enrolled match, so the sole UNACCOUNTED hint (marco) maps 1:1 to the unknown voice.
        var stage = Stage(store, PolicyPhase.GradedAutoApply, Hints("guilhem", "marco"));

        var result = await stage.ResolveAsync(Rec,
            [Cluster("s1", TestVectors.Axis(0)), Cluster("s2", TestVectors.Axis(30))]);

        Assert.Equal("guilhem", result.Verdicts[0].Person);   // s1 → enrolled guilhem
        Assert.Equal("marco", result.Verdicts[1].Person);     // s2 → the unaccounted hint marco
        var proposal = Assert.Single(result.EnrollmentProposals);
        Assert.Equal("marco", proposal.PersonSlug);           // only marco is proposed for enrollment
    }

    // ── 3. UNKNOWN ───────────────────────────────────────────────────────────────────────────────────

    [Fact] // no enrolled match + no hint → recording-LOCAL "Unknown speaker N", stable per cluster index,
           //  no map write, no fabrication
    public async Task Unknown_no_hint_yields_a_stable_local_label_and_no_write()
    {
        var store = await EnrolledStore(); // nobody enrolled
        var stage = Stage(store, PolicyPhase.GradedAutoApply); // no hints wired

        var result = await stage.ResolveAsync(Rec,
            [Cluster("s1", TestVectors.Axis(10)), Cluster("s2", TestVectors.Axis(50))]);

        Assert.All(result.Verdicts, v =>
        {
            Assert.Equal(AttributionOutcome.Omitted, v.Outcome); // no map write
            Assert.Null(v.Person);                               // never fabricated
        });
        Assert.Equal("Unknown speaker 1", result.Verdicts[0].LocalUnknownLabel); // deterministic per index
        Assert.Equal("Unknown speaker 2", result.Verdicts[1].LocalUnknownLabel);
        Assert.Empty(result.EnrollmentProposals);
    }

    [Fact] // the local unknown label is STABLE per (recordingId, clusterIndex) — a re-run gives the same label
    public async Task The_local_unknown_label_is_stable_across_runs()
    {
        var store = await EnrolledStore();
        var stage = Stage(store, PolicyPhase.EscalateOnly);
        var clusters = new[] { Cluster("s1", TestVectors.Axis(10)), Cluster("s2", TestVectors.Axis(50)) };

        var run1 = await stage.ResolveAsync(Rec, clusters);
        var run2 = await stage.ResolveAsync(Rec, clusters);
        Assert.Equal(run1.Verdicts[0].LocalUnknownLabel, run2.Verdicts[0].LocalUnknownLabel);
        Assert.Equal(run1.Verdicts[1].LocalUnknownLabel, run2.Verdicts[1].LocalUnknownLabel);
    }

    // ── 4. AMBIGUOUS ─────────────────────────────────────────────────────────────────────────────────

    [Fact] // 2 unmatched voices AND 2 unaccounted hinted participants → a SINGLE escalate-only open-point
           //  per voice, no fabrication
    public async Task Ambiguous_two_and_two_escalates_without_fabrication()
    {
        var store = await EnrolledStore(); // nobody enrolled
        var stage = Stage(store, PolicyPhase.GradedAutoApply, Hints("marco", "mara"));

        var result = await stage.ResolveAsync(Rec,
            [Cluster("s1", TestVectors.Axis(30)), Cluster("s2", TestVectors.Axis(60))]);

        Assert.All(result.Verdicts, v =>
        {
            Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome); // escalated, never auto-applied
            Assert.Null(v.Person);                                 // no fabrication
            Assert.Contains("which participant", v.Reason, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Empty(result.EnrollmentProposals); // nothing proposed in the ambiguous case
    }

    // ── worst case ───────────────────────────────────────────────────────────────────────────────────

    [Fact] // an empty recording (no clusters) → an empty result, no throw
    public async Task An_empty_recording_yields_an_empty_result()
    {
        var stage = Stage(await EnrolledStore(), PolicyPhase.EscalateOnly);
        var result = await stage.ResolveAsync(Rec, Array.Empty<MergedCluster>());
        Assert.Empty(result.Verdicts);
        Assert.Empty(result.EnrollmentProposals);
    }
}
