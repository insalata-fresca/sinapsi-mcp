using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Policy;

/// <summary>
/// Config-driven band thresholds for the grounded-autonomy decision policy (spec
/// <c>voiceprint-store</c> → "Threshold 0.62 with a mandatory re-fit plan"; DESIGN §5.1). Held
/// as CONFIGURATION, not code — the E0.5 defaults are auto ≥ 0.62 · review 0.45–0.62 · reject
/// &lt; 0.45. Re-fitting the threshold (E5) changes only this value object, never engine code.
/// </summary>
public sealed record DecisionBands
{
    /// <summary>E0.5 auto-apply floor: cosine ≥ this is the auto band.</summary>
    public const double DefaultAutoBand = 0.62;

    /// <summary>E0.5 reject ceiling: cosine &lt; this is the reject band (omit, never guess).</summary>
    public const double DefaultRejectBand = 0.45;

    public DecisionBands(double autoBand = DefaultAutoBand, double rejectBand = DefaultRejectBand)
    {
        if (rejectBand <= 0 || rejectBand >= 1)
            throw new ArgumentOutOfRangeException(nameof(rejectBand), rejectBand, "reject band must be in (0,1)");
        if (autoBand <= rejectBand || autoBand >= 1)
            throw new ArgumentOutOfRangeException(nameof(autoBand), autoBand,
                "auto band must be in (rejectBand, 1)");
        AutoBand = autoBand;
        RejectBand = rejectBand;
    }

    /// <summary>The E0.5 defaults (auto 0.62 / reject 0.45).</summary>
    public static DecisionBands Default { get; } = new();

    public double AutoBand { get; }
    public double RejectBand { get; }

    /// <summary>Whether a cosine sits in the auto band (≥ auto).</summary>
    public bool IsAuto(double cosine) => cosine >= AutoBand;

    /// <summary>Whether a cosine sits in the review band (reject ≤ cosine &lt; auto).</summary>
    public bool IsReview(double cosine) => cosine >= RejectBand && cosine < AutoBand;

    /// <summary>Whether a cosine sits in the reject band (&lt; reject).</summary>
    public bool IsReject(double cosine) => cosine < RejectBand;
}

/// <summary>
/// The phase mode of the decision policy (spec <c>speaker-attribution</c> → "First-concept
/// escalate-only configuration"; DESIGN §11.0). In the FIRST CONCEPT the policy runs
/// escalate-only: every band routes to <c>open_point</c> so NOTHING auto-applies until the
/// threshold is re-fit and validated (E5). The graded bands become active only at P5 — by
/// CONFIGURATION, without any code change.
/// </summary>
public enum PolicyPhase
{
    /// <summary>First concept: every attribution (even a 0.90 match) → <c>open_point</c>.</summary>
    EscalateOnly,

    /// <summary>P5 (post re-fit + validation): graded auto / flag / escalate bands take effect.</summary>
    GradedAutoApply,
}

/// <summary>
/// The grounded-autonomy attribution decision policy (spec <c>speaker-attribution</c> →
/// "Grounded-autonomy attribution decision policy"; DESIGN §5.1). It routes each merged cluster's
/// <see cref="AttributionCandidate"/> to exactly one <see cref="AttributionOutcome"/>, applying
/// the E0.5 bands. The hard floor: NEVER invent, NEVER guess — a match with no basis (below the
/// auto band, ambiguous, or contradicted) is escalated or omitted, never asserted.
///
/// <para>Escalate-only phase gate (the first-concept default): when <see cref="Phase"/> is
/// <see cref="PolicyPhase.EscalateOnly"/>, EVERY resolvable/ambiguous cluster becomes an
/// <c>open_point</c> — the graded auto/flag decision is computed but overridden to escalate,
/// proving the gate. An omit (below-reject, no prior) still omits (there is nothing to escalate;
/// escalating a non-identification would create a phantom question).</para>
/// </summary>
public sealed class DecisionPolicy(
    DecisionBands? bands = null,
    PolicyPhase phase = PolicyPhase.EscalateOnly,
    string autoBasisVersion = "v1")
{
    private readonly DecisionBands _bands = bands ?? DecisionBands.Default;
    private readonly string _autoBasisVersion = string.IsNullOrWhiteSpace(autoBasisVersion)
        ? throw new ArgumentException("autoBasisVersion must be non-empty", nameof(autoBasisVersion))
        : autoBasisVersion;

    public DecisionBands Bands => _bands;
    public PolicyPhase Phase { get; } = phase;

    /// <summary>
    /// Decide the outcome for one candidate, producing a source ref
    /// <c>rec://&lt;recordingId&gt;#&lt;mergedSpeaker&gt;</c> for an applied outcome.
    /// </summary>
    public AttributionVerdict Decide(AttributionCandidate candidate, string recordingId)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));

        // 1a) COLD START / NOVEL SPEAKER: no voiceprint enrolled to compare against
        //     (BestMatchPerson is null) AND no prior contradicts. This is a REAL person we
        //     cannot yet name — not a non-identification. Escalate it to an open-point so the
        //     who's-who bootstrap can start; answering enrolls the voiceprint. This is a
        //     GROUNDED QUESTION, never a guess (no Person/basis is asserted), so it stays inside
        //     the never-guess floor. It is NOT overridden by escalate-only (already withheld).
        //     A CONFLICTING strong prior (e.g. it names a DELETED person — lint R8) is excluded
        //     here so a tombstoned person is never re-surfaced; that falls through to omit (1b).
        if (candidate.BestMatchPerson is null
            && candidate.PriorRelation != PriorRelation.Agrees
            && candidate.PriorRelation != PriorRelation.Conflicts)
            return AttributionVerdict.OpenPoint(candidate.MergedSpeaker, 0.0,
                $"who is speaker {candidate.MergedSpeaker} in recording {recordingId}? "
                + "(no enrolled voiceprint yet — name to enroll"
                + (candidate.PriorPerson is null ? ")" : $"; filename/participant prior suggests {candidate.PriorPerson})"));

        // 1b) Remaining below-reject / no-live-match with no confirming prior → OMIT (either a
        //     NON-NULL voiceprint we compared and it genuinely isn't them, or a null match whose
        //     only prior CONFLICTS — a tombstoned/other person we must not re-surface). Escalating
        //     would create a phantom question. Checked FIRST and NOT overridden by escalate-only.
        var noVoiceMatch = candidate.BestMatchPerson is null || _bands.IsReject(candidate.BestMatchCosine);
        if (noVoiceMatch && candidate.PriorRelation != PriorRelation.Agrees)
            return AttributionVerdict.Omitted(candidate.MergedSpeaker,
                $"best match {Describe(candidate)} below reject band {_bands.RejectBand:0.###} and no confirming prior → unidentified");

        // 2) Compute the *graded* verdict per the band table (DESIGN §5.1), independent of phase.
        var graded = DecideGraded(candidate, recordingId);

        // 3) Escalate-only phase gate: in the first concept, an otherwise-applied verdict is
        //    downgraded to an open-point so nothing auto-applies until the threshold is validated.
        if (Phase == PolicyPhase.EscalateOnly && graded.IsApplied)
            return AttributionVerdict.OpenPoint(candidate.MergedSpeaker, graded.Confidence,
                $"escalate-only phase: withholding {graded.Outcome} ({graded.Person} @ {graded.Confidence:0.###}) — auto-apply disabled until threshold re-fit + validated");

        return graded;
    }

    /// <summary>
    /// The graded band decision (DESIGN §5.1), independent of the phase gate:
    /// <list type="bullet">
    /// <item>≥ auto AND (prior agrees OR no conflicting prior) AND single ≥-auto match → auto-apply</item>
    /// <item>review band, OR ≥ auto conflicting a strong prior, OR two enrolled ≥ auto → open-point</item>
    /// </list>
    /// </summary>
    private AttributionVerdict DecideGraded(AttributionCandidate c, string recordingId)
    {
        var best = c.BestMatchCosine;
        var person = c.BestMatchPerson;

        // Reject band or no match → NEVER auto-apply. Reaching here with a below-reject cosine means
        // the omit guard was skipped ONLY because a prior AGREES (step 1). A prior is a *confirming*
        // signal, never a standalone identity basis (DESIGN §5.1: auto requires cosine ≥ auto band) —
        // so a below-reject match + agreeing prior ESCALATES to the operator, it does not assert an
        // identity at ~0 voice confidence. This is the no-guess floor holding at the policy seam.
        if (person is null || _bands.IsReject(best))
            return AttributionVerdict.OpenPoint(c.MergedSpeaker, best,
                $"below-reject voice match {Describe(c)} with only a prior → operator confirmation required (a prior never auto-applies alone)");

        // Review band → escalate (a low-confidence match is never auto-applied).
        if (_bands.IsReview(best))
            return AttributionVerdict.OpenPoint(c.MergedSpeaker, best,
                $"review-band match {Describe(c)} → operator confirmation required");

        // From here best is in the auto band.
        // Two enrolled people both ≥ auto → ambiguous → escalate.
        if (c.SecondMatchPerson is not null && _bands.IsAuto(c.SecondMatchCosine))
            return AttributionVerdict.OpenPoint(c.MergedSpeaker, best,
                $"ambiguous: {person} @ {best:0.###} and {c.SecondMatchPerson} @ {c.SecondMatchCosine:0.###} both in the auto band");

        // ≥ auto but CONFLICTS with a strong prior → escalate (never silently resolve to the voice match).
        if (c.PriorRelation == PriorRelation.Conflicts)
            return AttributionVerdict.OpenPoint(c.MergedSpeaker, best,
                $"conflict: voice matches {person} @ {best:0.###} but a strong prior indicates {c.PriorPerson} → operator confirmation required");

        // ≥ auto, single match, prior agrees or no conflict → AUTO-APPLY with an auto basis.
        var basis = ConfirmationBasis.Auto(_autoBasisVersion);
        var source = $"rec://{recordingId}#{c.MergedSpeaker}";
        return AttributionVerdict.AutoApplied(c.MergedSpeaker, person, best, source, basis);
    }

    private static string Describe(AttributionCandidate c) =>
        c.BestMatchPerson is null ? "(none)" : $"{c.BestMatchPerson} @ {c.BestMatchCosine:0.###}";
}
