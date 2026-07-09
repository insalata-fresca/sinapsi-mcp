using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Harness;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// MISSION M6 — the autonomous-first flip (auto-apply + auto-enroll), shipped DARK behind validation.
/// These tests prove the SAFETY posture with the flag OFF and via a SIMULATED flag-on — NEVER by
/// shipping autonomy on. The five headline proofs the mission demands:
/// <list type="letter">
///   <item>(a) EscalateOnly (the default) writes NOTHING;</item>
///   <item>(b) GradedAutoApply auto-applies ONLY confident + policy-vetted attributions;</item>
///   <item>(c) an ambiguous/conflicting participant hint CANNOT auto-apply, even under GradedAutoApply;</item>
///   <item>(d) an off-§10-allowlist auto-enroll is REFUSED (never written);</item>
///   <item>(e) auto-enroll enrolls the CORRECT voiceprint (the hinted voice, tied to the hinted person).</item>
/// </list>
/// Synthetic 192-d vectors + in-memory adapters only — no personal audio, no biometric vectors.
/// </summary>
public sealed class M6AutonomousFlipTests
{
    private const string Rec = "20260709-standup";
    private static readonly DateOnly Day = new(2026, 7, 9);

    private static MergedCluster Cluster(string speaker, float[] centroid) =>
        new(speaker, [speaker], centroid, [new DiarizedSegment(speaker, 0, 5)]);

    private static IParticipantHintSource Hints(params string[] participants) =>
        new InMemoryParticipantHintSource(new Dictionary<string, IReadOnlyList<string>> { [Rec] = participants });

    private static AttributionStage Stage(
        IVoiceprintStore store, PolicyPhase phase, IParticipantHintSource? hints = null) =>
        new(store, new DecisionPolicy(DecisionBands.Default, phase), hints);

    private static async Task<InMemoryVoiceprintStore> EnrolledStore(
        EnrollmentAllowlist allowlist, params (string slug, float[] vec)[] people)
    {
        var store = new InMemoryVoiceprintStore(allowlist);
        var e = new VoiceprintEnrollment(store);
        foreach (var (slug, vec) in people)
            await e.EnrollOnConfirmationAsync(slug, vec, [$"rec://seed#{slug}"], null,
                ConfirmationBasis.Human($"seed-{slug}"), Day);
        return store;
    }

    // ═══ ITEM 1 — the hint routes THROUGH DecisionPolicy (the bypass is closed) ═══════════════════════

    [Fact] // the participant-hint auto-apply now carries the participant-hint auto basis issued by the
           //  policy's DecideParticipantHint — not a hand-built verdict bypassing the policy.
    public void Policy_vets_the_participant_hint_and_issues_a_metadata_basis_under_graded()
    {
        var policy = new DecisionPolicy(DecisionBands.Default, PolicyPhase.GradedAutoApply);
        var v = policy.DecideParticipantHint("s1", Rec, "marco", 0.5);

        Assert.Equal(AttributionOutcome.AutoApplied, v.Outcome);
        Assert.Equal("marco", v.Person);
        Assert.Equal(ConfirmationBasis.ParticipantHintRule, v.Basis!.Rule);
        Assert.Equal($"rec://{Rec}#s1", v.SourceRef);
    }

    [Fact] // (c) an AMBIGUOUS hint (two enrolled voiceprints both ≥ auto for the voice) CANNOT auto-apply
           //  — even under a SIMULATED GradedAutoApply. Proven at the policy seam.
    public void An_ambiguous_hint_cannot_auto_apply_even_under_graded()
    {
        var policy = new DecisionPolicy(DecisionBands.Default, PolicyPhase.GradedAutoApply);
        var v = policy.DecideParticipantHint(
            "s1", Rec, "marco", 0.5, conflictingEnrolledPerson: null, secondEnrolledPerson: "someone-else");

        Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome);
        Assert.Null(v.Person);
        Assert.Null(v.Basis);
        Assert.Contains("ambiguous", v.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // (c) a CONFLICTING hint (a strong enrolled voiceprint names a DIFFERENT person) CANNOT
           //  auto-apply even under GradedAutoApply — the voice signal wins the tie to an open-point.
    public void A_conflicting_hint_cannot_auto_apply_even_under_graded()
    {
        var policy = new DecisionPolicy(DecisionBands.Default, PolicyPhase.GradedAutoApply);
        var v = policy.DecideParticipantHint(
            "s1", Rec, "marco", 0.5, conflictingEnrolledPerson: "guilhem");

        Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome);
        Assert.Null(v.Person);
        Assert.Contains("conflict", v.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // an empty hinted person is REFUSED (never fabricates a name).
    public void An_empty_hinted_person_throws_never_fabricates()
    {
        var policy = new DecisionPolicy(DecisionBands.Default, PolicyPhase.GradedAutoApply);
        Assert.Throws<ArgumentException>(() => policy.DecideParticipantHint("s1", Rec, "  ", 0.5));
    }

    // ═══ ITEM 2 — auto-enroll coordinator: (a) dark default, (d) allowlist, (e) correct voiceprint ═════

    // Build the attribution result the coordinator consumes: a 1:1 hint under the given phase.
    private static async Task<(AttributionResult result, InMemoryVoiceprintStore store)> HintResultAsync(
        PolicyPhase phase, EnrollmentAllowlist allowlist)
    {
        var store = await EnrolledStore(allowlist); // nobody enrolled — the voice is unmatched
        var stage = Stage(store, phase, Hints("marco"));
        var result = await stage.ResolveAsync(Rec, [Cluster("s1", TestVectors.Axis(30))]);
        return (result, store);
    }

    [Fact] // (a) under the DEFAULT EscalateOnly the coordinator writes NOTHING — the proposal stays a
           //  proposal, and no voiceprint is enrolled. This is the dark-by-default guarantee.
    public async Task Escalate_only_auto_enroll_writes_nothing()
    {
        var allowlist = new EnrollmentAllowlist(["marco"]); // even ON the allowlist …
        var (result, store) = await HintResultAsync(PolicyPhase.EscalateOnly, allowlist);
        var coord = new AutoEnrollmentCoordinator(new VoiceprintEnrollment(store), allowlist, PolicyPhase.EscalateOnly);

        Assert.False(coord.Enabled); // the dark gate
        var enrolled = await coord.AutoEnrollAsync(Rec, result, Day);

        Assert.Empty(enrolled);                                   // … nothing was written
        Assert.Null(await store.GetAsync("marco"));               // marco is NOT enrolled
        // The proposal still exists (logged, never written) — the escalate-only verdict is an open-point.
        Assert.Single(result.EnrollmentProposals);
        Assert.Equal(AttributionOutcome.OpenPoint, result.Verdicts[0].Outcome);
    }

    [Fact] // (e) under a SIMULATED GradedAutoApply + a 1:1 hint + the allowlist, the coordinator enrolls
           //  the CORRECT voiceprint: the hinted voice's centroid, tied to the hinted person.
    public async Task Graded_auto_enroll_enrolls_the_correct_voiceprint()
    {
        var allowlist = new EnrollmentAllowlist(["marco"]);
        var (result, store) = await HintResultAsync(PolicyPhase.GradedAutoApply, allowlist);
        var coord = new AutoEnrollmentCoordinator(new VoiceprintEnrollment(store), allowlist, PolicyPhase.GradedAutoApply);

        Assert.True(coord.Enabled);
        var enrolled = await coord.AutoEnrollAsync(Rec, result, Day);

        Assert.Equal(["marco"], enrolled);
        var print = await store.GetAsync("marco");
        Assert.NotNull(print);
        Assert.Equal("marco", print!.PersonSlug);
        // The CORRECT voiceprint: the enrolled centroid is the hinted voice cluster's centroid (Axis 30),
        // NOT some other person's — a match of that voice against the store now returns marco ≥ auto.
        var back = await store.MatchAsync(TestVectors.Axis(30));
        Assert.Equal("marco", back[0].PersonSlug);
        Assert.True(back[0].Cosine >= DecisionBands.DefaultAutoBand);
    }

    [Fact] // (d) an OFF-ALLOWLIST auto-enroll is REFUSED — the voiceprint is never written, and the
           //  drain is not broken (the coordinator logs + skips, never throws).
    public async Task Off_allowlist_auto_enroll_is_refused()
    {
        var allowlist = EnrollmentAllowlist.Empty; // marco is NOT consented
        var (result, store) = await HintResultAsync(PolicyPhase.GradedAutoApply, allowlist);
        var coord = new AutoEnrollmentCoordinator(new VoiceprintEnrollment(store), allowlist, PolicyPhase.GradedAutoApply);

        var enrolled = await coord.AutoEnrollAsync(Rec, result, Day); // must not throw

        Assert.Empty(enrolled);                       // nothing written
        Assert.Null(await store.GetAsync("marco"));   // marco is NOT enrolled (refused)
    }

    [Fact] // (b) a WITHHELD hint (escalate-only open-point verdict) carries no auto-applied verdict, so
           //  even a coordinator that IS enabled writes nothing for it (only confident + vetted writes).
    public async Task Graded_coordinator_does_not_write_for_a_withheld_verdict()
    {
        var allowlist = new EnrollmentAllowlist(["marco"]);
        // The RESULT was produced under EscalateOnly (verdict = open-point), but we run a GradedAutoApply
        // coordinator over it: there is no auto-applied participant-hint verdict → nothing is written.
        var (escalateResult, store) = await HintResultAsync(PolicyPhase.EscalateOnly, allowlist);
        var coord = new AutoEnrollmentCoordinator(new VoiceprintEnrollment(store), allowlist, PolicyPhase.GradedAutoApply);

        var enrolled = await coord.AutoEnrollAsync(Rec, escalateResult, Day);

        Assert.Empty(enrolled);
        Assert.Null(await store.GetAsync("marco"));
    }

    // ═══ ITEM 3 — the phase DEFAULTS to EscalateOnly; GradedAutoApply is flag-gated OFF ════════════════

    [Fact] // the config flag DEFAULTS to false → the policy is EscalateOnly (dark) with an empty env.
    public void Graded_auto_apply_flag_defaults_off()
    {
        var cfg = EnrichmentConfig.From(_ => null); // empty environment (production default)
        Assert.False(cfg.GradedAutoApply);           // the flag ships OFF
        var phase = cfg.GradedAutoApply ? PolicyPhase.GradedAutoApply : PolicyPhase.EscalateOnly;
        Assert.Equal(PolicyPhase.EscalateOnly, phase);
    }

    [Fact] // the flag is reachable ONLY via the explicit env var — flipping it is config, not code.
    public void Graded_auto_apply_flag_flips_only_via_the_explicit_env_var()
    {
        var on = EnrichmentConfig.From(k => k == "CERVELLO_GRADED_AUTO_APPLY" ? "true" : null);
        Assert.True(on.GradedAutoApply);
        var off = EnrichmentConfig.From(k => k == "CERVELLO_GRADED_AUTO_APPLY" ? "false" : null);
        Assert.False(off.GradedAutoApply);
    }

    // ═══ ITEM 4 — the validation harness measures accuracy vs a CONFIG bar (never hard-coded passed) ═══

    // A held-out set: one enrolled speaker (guilhem, clean match) + one hinted speaker (marco). We wire
    // the stage under a SIMULATED GradedAutoApply purely to MEASURE what auto-apply would do.
    private static async Task<(AttributionStage stage, IReadOnlyList<LabeledRecording> heldOut)> HeldOutAsync()
    {
        var store = await EnrolledStore(new EnrollmentAllowlist(["guilhem"]), ("guilhem", TestVectors.Axis(0)));
        var stage = Stage(store, PolicyPhase.GradedAutoApply, Hints("marco"));

        var guilhemVoice = Cluster("s1", TestVectors.TiltedFromAxis(0, 5, 0.9)); // → enrolled guilhem
        var marcoVoice = Cluster("s2", TestVectors.Axis(30));                     // → hinted marco (1:1)
        var heldOut = new[]
        {
            new LabeledRecording(Rec,
                [
                    new LabeledCluster(guilhemVoice, "guilhem", trueSpeakerEnrolled: true),
                    new LabeledCluster(marcoVoice, "marco", trueSpeakerEnrolled: false),
                ],
                appliedCorrections: 4, correctAppliedCorrections: 4),
        };
        return (stage, heldOut);
    }

    [Fact] // the harness measures enrolled TPR/FPR + hint accuracy + correction precision and computes
           //  PASS against the operator's bar — the bar is a PARAMETER, not a hard-coded "passed".
    public async Task Validation_harness_measures_accuracy_against_a_config_bar()
    {
        var (stage, heldOut) = await HeldOutAsync();
        var report = await AttributionValidationHarness.ValidateAsync(
            stage, heldOut, AttributionAccuracyBar.Reference);

        Assert.Equal(1, report.RecordingsEvaluated);
        Assert.Equal(1.0, report.EnrolledTpr, 3);          // guilhem attributed correctly
        Assert.Equal(0.0, report.EnrolledFpr, 3);          // no wrong voice-match
        Assert.Equal(1.0, report.HintAccuracy, 3);         // marco assigned correctly
        Assert.Equal(1.0, report.CorrectionPrecision, 3);  // all applied corrections correct
        Assert.True(report.Passed);
        Assert.Equal(PolicyPhase.GradedAutoApply, report.RecommendedPhase);
    }

    [Fact] // the bar is NOT hard-coded passed: an IMPOSSIBLE bar (perfect precision required, but the
           //  labeled set has a wrong applied correction) FAILS and the harness stays escalate-only.
    public async Task Validation_harness_fails_a_bar_the_data_does_not_clear()
    {
        var (stage, _) = await HeldOutAsync();
        // A held-out recording where a correction was applied WRONGLY (precision 0.5): below any sane bar.
        var badCorrections = new[]
        {
            new LabeledRecording(Rec,
                [
                    new LabeledCluster(Cluster("s2", TestVectors.Axis(30)), "marco", trueSpeakerEnrolled: false),
                ],
                appliedCorrections: 2, correctAppliedCorrections: 1),
        };

        var report = await AttributionValidationHarness.ValidateAsync(
            stage, badCorrections, AttributionAccuracyBar.Reference);

        Assert.False(report.Passed);                                   // did NOT clear the bar
        Assert.Equal(PolicyPhase.EscalateOnly, report.RecommendedPhase); // gate stays dark
        Assert.Equal(0.5, report.CorrectionPrecision, 3);
    }

    [Fact] // the reference bar is a documented DEFAULT, not an authorization — the operator sets the real
           //  values. The report never enables the flag; it only recommends.
    public void The_accuracy_bar_is_a_parameter_set_with_the_operator()
    {
        Assert.Equal(0.95, AttributionAccuracyBar.Reference.MinEnrolledTpr, 3);
        Assert.Equal(0.02, AttributionAccuracyBar.Reference.MaxEnrolledFpr, 3);
        Assert.Equal(0.95, AttributionAccuracyBar.Reference.MinHintAccuracy, 3);
        Assert.Equal(0.98, AttributionAccuracyBar.Reference.MinCorrectionPrecision, 3);
        // The operator can set a stricter or looser bar — it is just a value object.
        var strict = new AttributionAccuracyBar(0.99, 0.0, 0.99, 1.0);
        Assert.Equal(0.99, strict.MinEnrolledTpr, 3);
    }
}
