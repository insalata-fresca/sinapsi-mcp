using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Policy;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The grounded-autonomy decision policy (spec <c>speaker-attribution</c> → "Grounded-autonomy
/// attribution decision policy" + "First-concept escalate-only configuration"; DESIGN §5.1). A
/// FIXTURE PER BAND and per escalate-trigger, plus the phase-gate proof: a 0.90 clean match
/// ESCALATES while auto-apply is off. No personal audio — only a synthetic cosine per candidate.
/// </summary>
public sealed class DecisionPolicyTests
{
    private const string Rec = "rec-guilhem-121";

    // A policy with graded auto-apply ON (P5) — used to prove the band table itself.
    private static DecisionPolicy Graded() => new(DecisionBands.Default, PolicyPhase.GradedAutoApply);

    // A policy in first-concept escalate-only mode (the default) — used to prove the phase gate.
    private static DecisionPolicy EscalateOnly() => new(DecisionBands.Default, PolicyPhase.EscalateOnly);

    private static AttributionCandidate Candidate(
        double best, string? bestPerson = "guilhem",
        double second = 0.0, string? secondPerson = null,
        PriorRelation prior = PriorRelation.None, string? priorPerson = null) =>
        new("s1", bestPerson, best, secondPerson, second, prior, priorPerson);

    // ---- BAND: auto ----------------------------------------------------------------------

    [Fact] // scenario: High-confidence match with agreeing prior auto-applies
    public void Auto_band_with_agreeing_prior_auto_applies_with_auto_basis()
    {
        var v = Graded().Decide(
            Candidate(0.83, "guilhem", prior: PriorRelation.Agrees, priorPerson: "guilhem"), Rec);

        Assert.Equal(AttributionOutcome.AutoApplied, v.Outcome);
        Assert.Equal("guilhem", v.Person);
        Assert.Equal(0.83, v.Confidence, 3);
        Assert.Equal($"rec://{Rec}#s1", v.SourceRef);
        Assert.NotNull(v.Basis);
        Assert.Equal(ConfirmationBasisKind.Auto, v.Basis!.Kind);
        Assert.Equal("auto://voice-match@v1", v.Basis.Id);
    }

    [Fact] // ≥ auto AND no conflicting prior (prior absent) → auto-apply
    public void Auto_band_with_no_prior_auto_applies()
    {
        var v = Graded().Decide(Candidate(0.70, "guilhem", prior: PriorRelation.None), Rec);
        Assert.Equal(AttributionOutcome.AutoApplied, v.Outcome);
        Assert.NotNull(v.Basis);
    }

    // ---- ESCALATE TRIGGER 1: review band -------------------------------------------------

    [Fact] // scenario: Review-band match escalates
    public void Review_band_escalates_to_open_point()
    {
        var v = Graded().Decide(Candidate(0.55, "guilhem"), Rec);
        Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome);
        Assert.Null(v.Person);
        Assert.Null(v.Basis); // withheld — no basis on an open-point
    }

    // ---- ESCALATE TRIGGER 2: high match conflicting a strong prior -----------------------

    [Fact] // scenario: Conflicting high match escalates
    public void High_match_conflicting_strong_prior_escalates_never_resolves_to_voice()
    {
        var v = Graded().Decide(
            Candidate(0.71, "personA", prior: PriorRelation.Conflicts, priorPerson: "personB"), Rec);

        Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome);
        Assert.Null(v.Person); // never silently resolved to A
        Assert.Contains("conflict", v.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- ESCALATE TRIGGER 3: two enrolled people both in the auto band -------------------

    [Fact] // scenario: Two enrolled people both in the auto band escalate
    public void Two_enrolled_both_in_auto_band_escalate()
    {
        var v = Graded().Decide(
            Candidate(0.66, "personA", second: 0.64, secondPerson: "personB"), Rec);

        Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome);
        Assert.Contains("ambiguous", v.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- BAND: omit (never guessed) ------------------------------------------------------

    [Fact] // scenario: Below-reject with no prior is omitted, never guessed
    public void Below_reject_no_prior_is_omitted_never_guessed()
    {
        var v = Graded().Decide(Candidate(0.30, "guilhem", prior: PriorRelation.None), Rec);
        Assert.Equal(AttributionOutcome.Omitted, v.Outcome);
        Assert.Null(v.Person);
        Assert.Null(v.Basis);
        Assert.Equal(0.0, v.Confidence);
    }

    [Fact] // COLD START: no voiceprint enrolled to compare (null best-match) → OPEN-POINT, not omit.
           // This is a real speaker we cannot yet name — the who's-who bootstrap must start here.
    public void Cold_start_null_best_match_opens_a_point_not_omitted()
    {
        var v = Graded().Decide(Candidate(0.0, bestPerson: null, prior: PriorRelation.None), Rec);
        Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome);
        Assert.Null(v.Person);     // never named — grounded question, not a guess
        Assert.Null(v.Basis);      // withheld — no basis on an open-point
        Assert.Contains("who is speaker", v.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Rec, v.Reason, StringComparison.Ordinal);
    }

    [Fact] // a NON-NULL match below the reject band with no prior still OMITS (we checked — it isn't them)
    public void Below_reject_non_null_match_no_prior_still_omits()
    {
        var v = Graded().Decide(Candidate(0.30, bestPerson: "guilhem", prior: PriorRelation.None), Rec);
        Assert.Equal(AttributionOutcome.Omitted, v.Outcome); // not a phantom question
        Assert.Null(v.Person);
    }

    [Fact] // COLD START holds under escalate-only too — a null best-match is an open-point, not omit
    public void Escalate_only_cold_start_null_best_match_opens_a_point()
    {
        var v = EscalateOnly().Decide(Candidate(0.0, bestPerson: null, prior: PriorRelation.None), Rec);
        Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome);
    }

    // ---- PHASE GATE (the mission's headline proof) ---------------------------------------

    [Fact] // scenario: Manual-only mode escalates everything — a 0.90 clean match still ESCALATES
    public void Escalate_only_phase_escalates_even_a_090_clean_match()
    {
        var candidate = Candidate(0.90, "guilhem", prior: PriorRelation.Agrees, priorPerson: "guilhem");

        // Same candidate would auto-apply under P5…
        Assert.Equal(AttributionOutcome.AutoApplied, Graded().Decide(candidate, Rec).Outcome);

        // …but in the first-concept escalate-only phase it is WITHHELD as an open-point.
        var v = EscalateOnly().Decide(candidate, Rec);
        Assert.Equal(AttributionOutcome.OpenPoint, v.Outcome);
        Assert.Null(v.Person);
        Assert.Null(v.Basis);
        Assert.Contains("escalate-only", v.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // escalate-only still OMITS a below-reject non-identification (nothing to escalate)
    public void Escalate_only_still_omits_below_reject_no_prior()
    {
        var v = EscalateOnly().Decide(Candidate(0.30, "guilhem", prior: PriorRelation.None), Rec);
        Assert.Equal(AttributionOutcome.Omitted, v.Outcome); // not a phantom open-point
    }

    [Fact] // scenario: P5 configuration enables graded bands — by config, no code change
    public void P5_configuration_enables_graded_bands_without_code_change()
    {
        var candidate = Candidate(0.83, "guilhem", prior: PriorRelation.Agrees, priorPerson: "guilhem");
        // Only the PolicyPhase differs between these two constructions (configuration).
        Assert.Equal(AttributionOutcome.OpenPoint, EscalateOnly().Decide(candidate, Rec).Outcome);
        Assert.Equal(AttributionOutcome.AutoApplied, Graded().Decide(candidate, Rec).Outcome);
    }

    // ---- band boundaries are config, re-fittable -----------------------------------------

    [Fact] // scenario: Threshold is config, re-fittable — a stricter auto band changes the verdict, no code change
    public void Threshold_is_config_refittable()
    {
        var candidate = Candidate(0.70, "guilhem", prior: PriorRelation.None);
        Assert.Equal(AttributionOutcome.AutoApplied,
            new DecisionPolicy(DecisionBands.Default, PolicyPhase.GradedAutoApply).Decide(candidate, Rec).Outcome);
        // Re-fit auto band up to 0.75 → the same 0.70 match now sits in the review band → escalate.
        Assert.Equal(AttributionOutcome.OpenPoint,
            new DecisionPolicy(new DecisionBands(autoBand: 0.75), PolicyPhase.GradedAutoApply).Decide(candidate, Rec).Outcome);
    }
}
