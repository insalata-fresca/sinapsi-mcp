using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline.Stages;

namespace Cervello.Enrichment.Harness;

/// <summary>
/// M6 item 4 — the ATTRIBUTION validation harness + the accuracy bar that keeps auto-apply DARK until
/// it's proven safe. Distinct from <see cref="HeldOutValidationHarness"/> (which measures raw
/// voiceprint-pair TPR/FPR at a candidate threshold): this harness runs a LABELED HELD-OUT set of whole
/// RECORDINGS through the REAL <see cref="AttributionStage"/> (the same code the drain runs) and measures
/// the three accuracy signals that govern un-darkening:
///
/// <list type="bullet">
///   <item><b>Enrolled-match TPR / FPR</b> — of the clusters whose true speaker IS enrolled, how many
///     the stage auto-applied to the CORRECT person (TPR) vs to a WRONG person (FPR).</item>
///   <item><b>Participant-hint assignment correctness</b> — of the clusters resolved via a participant
///     hint, the fraction assigned to the RIGHT person.</item>
///   <item><b>Correction precision</b> — of the correction diffs applied over the held-out set, the
///     fraction that match the labeled expected correction (supplied by the caller's ground truth).</item>
/// </list>
///
/// <para><b>The bar is a PARAMETER, set later with the operator — never a hard-coded "passed".</b> The
/// harness EMITS the measured metrics + a pass/fail computed AGAINST the caller-supplied
/// <see cref="AttributionAccuracyBar"/>; it does not itself decide the bar values, and the decision to
/// flip <c>CERVELLO_GRADED_AUTO_APPLY</c> stays with the operator. This is the gate that proves the flip
/// is safe BEFORE it's on: a run that does not clear the bar recommends staying escalate-only.</para>
///
/// <para>Runs the stage under a SIMULATED <c>GradedAutoApply</c> policy purely to MEASURE what auto-apply
/// WOULD do — it writes nothing, enrolls nothing, and never enables the production flag. The caller
/// passes an <see cref="AttributionStage"/> already wired to a graded-phase policy over the enrolled set.</para>
/// </summary>
public static class AttributionValidationHarness
{
    /// <summary>
    /// Validate attribution accuracy over a labeled held-out set of recordings and compute pass/fail
    /// vs <paramref name="bar"/>. The <paramref name="stage"/> is the real attribution stage (wired to a
    /// SIMULATED graded-phase policy — measurement only, no production flip).
    /// </summary>
    public static async Task<AttributionValidationReport> ValidateAsync(
        AttributionStage stage,
        IReadOnlyList<LabeledRecording> heldOut,
        AttributionAccuracyBar bar,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(heldOut);
        ArgumentNullException.ThrowIfNull(bar);
        if (heldOut.Count == 0)
            throw new ArgumentException("attribution validation needs at least one held-out recording", nameof(heldOut));

        // Enrolled-match counters.
        var enrolledTruePos = 0;   // true speaker enrolled AND auto-applied to the correct person
        var enrolledCases = 0;     // clusters whose true speaker is enrolled
        var enrolledFalsePos = 0;  // auto-applied to a person who is NOT the true speaker
        var appliedEnrolled = 0;   // auto-applied via a voice-match basis (the FPR denominator)

        // Participant-hint counters.
        var hintCorrect = 0;
        var hintCases = 0;

        // Correction-precision counters (supplied ground truth per recording).
        var correctionCorrect = 0;
        var correctionApplied = 0;

        foreach (var rec in heldOut)
        {
            var mergedClusters = rec.Clusters.Select(c => c.Cluster).ToList();
            var result = await stage.ResolveAsync(rec.RecordingId, mergedClusters, ct).ConfigureAwait(false);

            for (var i = 0; i < result.Verdicts.Count; i++)
            {
                var v = result.Verdicts[i];
                var truth = rec.Clusters[i];
                var trueSpeaker = truth.TrueSpeaker;      // the labeled ground-truth person (or null = truly unknown)
                var trueSpeakerEnrolled = truth.TrueSpeakerEnrolled;

                var isVoiceMatch = v.Outcome == AttributionOutcome.AutoApplied
                    && v.Basis is { Rule: ConfirmationBasis.VoiceMatchRule };
                var isHint = v.Outcome == AttributionOutcome.AutoApplied
                    && v.Basis is { Rule: ConfirmationBasis.ParticipantHintRule };

                // ── Enrolled-match TPR/FPR (only clusters whose true speaker is enrolled count for TPR) ──
                if (trueSpeakerEnrolled && trueSpeaker is not null)
                {
                    enrolledCases++;
                    if (isVoiceMatch && string.Equals(v.Person, trueSpeaker, StringComparison.Ordinal))
                        enrolledTruePos++;
                }
                if (isVoiceMatch)
                {
                    appliedEnrolled++;
                    // A voice-match auto-apply to someone OTHER than the true speaker is a false positive
                    // (including naming a truly-unknown speaker at all).
                    if (trueSpeaker is null || !string.Equals(v.Person, trueSpeaker, StringComparison.Ordinal))
                        enrolledFalsePos++;
                }

                // ── Participant-hint assignment correctness ──────────────────────────────────────
                if (isHint)
                {
                    hintCases++;
                    if (trueSpeaker is not null && string.Equals(v.Person, trueSpeaker, StringComparison.Ordinal))
                        hintCorrect++;
                }
            }

            // ── Correction precision (ground truth carried per recording; independent of clusters) ──
            correctionApplied += rec.AppliedCorrections;
            correctionCorrect += rec.CorrectAppliedCorrections;
        }

        var enrolledTpr = enrolledCases == 0 ? 1.0 : (double)enrolledTruePos / enrolledCases;
        var enrolledFpr = appliedEnrolled == 0 ? 0.0 : (double)enrolledFalsePos / appliedEnrolled;
        var hintAccuracy = hintCases == 0 ? 1.0 : (double)hintCorrect / hintCases;
        var correctionPrecision = correctionApplied == 0 ? 1.0 : (double)correctionCorrect / correctionApplied;

        var passed =
            enrolledTpr >= bar.MinEnrolledTpr &&
            enrolledFpr <= bar.MaxEnrolledFpr &&
            hintAccuracy >= bar.MinHintAccuracy &&
            correctionPrecision >= bar.MinCorrectionPrecision;

        var recommendedPhase = passed ? Policy.PolicyPhase.GradedAutoApply : Policy.PolicyPhase.EscalateOnly;
        var reason = passed
            ? "held-out attribution accuracy CLEARS the operator's bar — graded auto-apply may be enabled (operator flips the flag)"
            : "held-out attribution accuracy BELOW the operator's bar — gate stays escalate-only (dark)";

        return new AttributionValidationReport(
            RecordingsEvaluated: heldOut.Count,
            EnrolledTpr: enrolledTpr,
            EnrolledFpr: enrolledFpr,
            EnrolledCases: enrolledCases,
            HintAccuracy: hintAccuracy,
            HintCases: hintCases,
            CorrectionPrecision: correctionPrecision,
            CorrectionsApplied: correctionApplied,
            Passed: passed,
            RecommendedPhase: recommendedPhase,
            Bar: bar,
            Reason: reason);
    }
}

/// <summary>
/// One labeled held-out RECORDING for the attribution validation harness: the recording id + its merged
/// clusters (each carrying the GROUND-TRUTH speaker via <see cref="LabeledCluster"/>) + the recording's
/// correction ground-truth counts. Synthetic labels only — no personal audio, no biometric vectors.
/// </summary>
public sealed record LabeledRecording
{
    public LabeledRecording(
        string recordingId,
        IReadOnlyList<LabeledCluster> clusters,
        int appliedCorrections = 0,
        int correctAppliedCorrections = 0)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("LabeledRecording.RecordingId must be non-empty", nameof(recordingId));
        ArgumentNullException.ThrowIfNull(clusters);
        if (appliedCorrections < 0)
            throw new ArgumentOutOfRangeException(nameof(appliedCorrections));
        if (correctAppliedCorrections < 0 || correctAppliedCorrections > appliedCorrections)
            throw new ArgumentOutOfRangeException(nameof(correctAppliedCorrections),
                "correct applied corrections must be within [0, appliedCorrections]");
        RecordingId = recordingId;
        Clusters = clusters;
        AppliedCorrections = appliedCorrections;
        CorrectAppliedCorrections = correctAppliedCorrections;
    }

    public string RecordingId { get; }

    /// <summary>The labeled merged clusters (ground-truth speaker per cluster).</summary>
    public IReadOnlyList<LabeledCluster> Clusters { get; }

    /// <summary>How many correction diffs were applied over this recording (the precision denominator).</summary>
    public int AppliedCorrections { get; }

    /// <summary>How many of the applied corrections match the labeled expected correction (the numerator).</summary>
    public int CorrectAppliedCorrections { get; }
}

/// <summary>
/// A labeled merged cluster for validation: the real <see cref="MergedCluster"/> the stage resolves,
/// plus the GROUND TRUTH — the true speaker slug (null = the speaker is genuinely unknown / not a person
/// the system should name) and whether that true speaker is enrolled. The harness compares the stage's
/// verdict against this truth.
/// </summary>
public sealed record LabeledCluster
{
    public LabeledCluster(MergedCluster cluster, string? trueSpeaker, bool trueSpeakerEnrolled)
    {
        Cluster = cluster ?? throw new ArgumentNullException(nameof(cluster));
        if (trueSpeakerEnrolled && string.IsNullOrWhiteSpace(trueSpeaker))
            throw new ArgumentException("an enrolled true speaker must be named", nameof(trueSpeaker));
        TrueSpeaker = string.IsNullOrWhiteSpace(trueSpeaker) ? null : trueSpeaker;
        TrueSpeakerEnrolled = trueSpeakerEnrolled;
    }

    public MergedCluster Cluster { get; }
    public string? TrueSpeaker { get; }
    public bool TrueSpeakerEnrolled { get; }
}

/// <summary>
/// The operator's attribution ACCURACY bar (M6 item 4) — the thresholds a held-out run must clear before
/// graded auto-apply may be enabled. Set WITH the operator; never a hard-coded "we passed". A run's
/// pass/fail is computed against these values, and the run does not itself decide them.
/// </summary>
public sealed record AttributionAccuracyBar
{
    public AttributionAccuracyBar(
        double minEnrolledTpr,
        double maxEnrolledFpr,
        double minHintAccuracy,
        double minCorrectionPrecision)
    {
        if (minEnrolledTpr is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minEnrolledTpr), minEnrolledTpr, "minEnrolledTpr in (0,1]");
        if (maxEnrolledFpr is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(maxEnrolledFpr), maxEnrolledFpr, "maxEnrolledFpr in [0,1)");
        if (minHintAccuracy is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minHintAccuracy), minHintAccuracy, "minHintAccuracy in (0,1]");
        if (minCorrectionPrecision is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minCorrectionPrecision), minCorrectionPrecision, "minCorrectionPrecision in (0,1]");
        MinEnrolledTpr = minEnrolledTpr;
        MaxEnrolledFpr = maxEnrolledFpr;
        MinHintAccuracy = minHintAccuracy;
        MinCorrectionPrecision = minCorrectionPrecision;
    }

    public double MinEnrolledTpr { get; }
    public double MaxEnrolledFpr { get; }
    public double MinHintAccuracy { get; }
    public double MinCorrectionPrecision { get; }

    /// <summary>
    /// A conservative REFERENCE bar (enrolled TPR ≥ 0.95, FPR ≤ 0.02, hint accuracy ≥ 0.95, correction
    /// precision ≥ 0.98). NOT an authorization to flip the flag — the operator sets the real bar and
    /// makes the call. Provided only so a run has a sane default to report against.
    /// </summary>
    public static AttributionAccuracyBar Reference { get; } = new(0.95, 0.02, 0.95, 0.98);
}

/// <summary>
/// The attribution validation result: the measured metrics + the computed pass/fail vs the bar + the
/// recommended phase. The operator reads this to decide whether to flip the graded-auto-apply flag; the
/// report itself never enables it.
/// </summary>
public sealed record AttributionValidationReport(
    int RecordingsEvaluated,
    double EnrolledTpr,
    double EnrolledFpr,
    int EnrolledCases,
    double HintAccuracy,
    int HintCases,
    double CorrectionPrecision,
    int CorrectionsApplied,
    bool Passed,
    Policy.PolicyPhase RecommendedPhase,
    AttributionAccuracyBar Bar,
    string Reason);
