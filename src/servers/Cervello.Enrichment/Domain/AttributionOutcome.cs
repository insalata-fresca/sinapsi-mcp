namespace Cervello.Enrichment.Domain;

/// <summary>
/// The four — and only four — outcomes of the grounded-autonomy decision policy for a speaker
/// attribution (spec <c>speaker-attribution</c> → "Grounded-autonomy attribution decision
/// policy"; DESIGN §5.1). The engine SHALL NEVER assert a speaker identity outside these, and
/// SHALL NEVER guess when evidence is absent or contradictory.
/// </summary>
public enum AttributionOutcome
{
    /// <summary>
    /// Best-match cosine ≥ auto band AND (a prior agrees OR no prior conflicts) AND no other
    /// enrolled person is also ≥ auto band. Applied, carrying confidence + <c>rec://</c> source
    /// + an <c>auto://voice-match@&lt;v&gt;</c> basis.
    /// </summary>
    AutoApplied,

    /// <summary>
    /// A minor, low-value attribution the engine is unsure of but whose wrong write is trivially
    /// reversible. Applied but marked <c>flagged</c> (used sparingly; NEVER for identity that
    /// drives enrollment — DESIGN §5.1).
    /// </summary>
    Flagged,

    /// <summary>
    /// High-value ambiguity — review-band match, a ≥ auto-band match that conflicts with a strong
    /// prior, or two enrolled people both ≥ auto band. The attribution is WITHHELD and an
    /// open-point enqueued (open-points-mcp).
    /// </summary>
    OpenPoint,

    /// <summary>
    /// Best-match cosine &lt; reject band and no confirming prior. The speaker is left
    /// "unidentified"; nothing is written and the gap is recorded (the no-fabrication floor).
    /// </summary>
    Omitted,
}

/// <summary>
/// The resolver's per-cluster input to the decision policy, before a verdict is taken
/// (discovery#domain-model → AttributionCandidate). Carries the best voiceprint match and how
/// the prior relates to it — never a verdict itself.
/// </summary>
public sealed record AttributionCandidate
{
    public AttributionCandidate(
        string mergedSpeaker,
        string? bestMatchPerson,
        double bestMatchCosine,
        string? secondMatchPerson,
        double secondMatchCosine,
        PriorRelation priorRelation,
        string? priorPerson)
    {
        if (string.IsNullOrWhiteSpace(mergedSpeaker))
            throw new ArgumentException("AttributionCandidate.MergedSpeaker must be non-empty", nameof(mergedSpeaker));
        MergedSpeaker = mergedSpeaker;
        BestMatchPerson = bestMatchPerson;
        BestMatchCosine = bestMatchCosine;
        SecondMatchPerson = secondMatchPerson;
        SecondMatchCosine = secondMatchCosine;
        PriorRelation = priorRelation;
        PriorPerson = priorPerson;
    }

    /// <summary>The merged cluster label being resolved.</summary>
    public string MergedSpeaker { get; }

    /// <summary>The best-matching enrolled person by cosine, or null if no voiceprint matched.</summary>
    public string? BestMatchPerson { get; }

    /// <summary>The best-match cosine (0 when no voiceprint enrolled / matched).</summary>
    public double BestMatchCosine { get; }

    /// <summary>The second-best enrolled person, used to detect the "two people ≥ auto band" ambiguity.</summary>
    public string? SecondMatchPerson { get; }

    /// <summary>The second-best cosine.</summary>
    public double SecondMatchCosine { get; }

    /// <summary>How the filename/participant prior relates to the best voice match.</summary>
    public PriorRelation PriorRelation { get; }

    /// <summary>The person the prior points at (for logging / open-point context), if any.</summary>
    public string? PriorPerson { get; }
}

/// <summary>How the org-chart/filename prior relates to the best voice match.</summary>
public enum PriorRelation
{
    /// <summary>No prior identifies this speaker (empty candidate set).</summary>
    None,

    /// <summary>The prior candidate set includes the best-match person (prior agrees).</summary>
    Agrees,

    /// <summary>The prior strongly indicates a DIFFERENT person than the best voice match (conflict).</summary>
    Conflicts,
}

/// <summary>
/// The decision policy verdict for one merged cluster: the outcome, the resolved person (when
/// applied), the confidence, the source ref, and the confirmation basis (only present for an
/// applied outcome — an <c>open_point</c>/<c>omitted</c> verdict carries none, satisfying the
/// "assistive-only until a basis" invariant).
/// </summary>
public sealed record AttributionVerdict
{
    private AttributionVerdict(
        string mergedSpeaker,
        AttributionOutcome outcome,
        string? person,
        double confidence,
        string? sourceRef,
        ConfirmationBasis? basis,
        string reason)
    {
        MergedSpeaker = mergedSpeaker;
        Outcome = outcome;
        Person = person;
        Confidence = confidence;
        SourceRef = sourceRef;
        Basis = basis;
        Reason = reason;
    }

    public string MergedSpeaker { get; }
    public AttributionOutcome Outcome { get; }

    /// <summary>The resolved person, present only for <c>auto_applied</c>/<c>flagged</c>.</summary>
    public string? Person { get; }

    /// <summary>The match cosine carried as the applied <c>confidence</c> (0 when omitted).</summary>
    public double Confidence { get; }

    /// <summary>The <c>rec://&lt;id&gt;#&lt;seg&gt;</c> source ref (applied outcomes only).</summary>
    public string? SourceRef { get; }

    /// <summary>The confirmation basis — non-null ONLY for an applied outcome (lint R9).</summary>
    public ConfirmationBasis? Basis { get; }

    /// <summary>Human/machine reason string (open-point context; recorded gap for omit).</summary>
    public string Reason { get; }

    /// <summary>Whether this verdict wrote (or would write) a fact to <c>map/</c>.</summary>
    public bool IsApplied => Outcome is AttributionOutcome.AutoApplied or AttributionOutcome.Flagged;

    public static AttributionVerdict AutoApplied(
        string mergedSpeaker, string person, double confidence, string sourceRef, ConfirmationBasis basis)
    {
        RequireApplied(person, sourceRef, basis);
        return new(mergedSpeaker, AttributionOutcome.AutoApplied, person, confidence, sourceRef, basis,
            $"auto-applied {person} @ {confidence:0.###}");
    }

    public static AttributionVerdict Flagged(
        string mergedSpeaker, string person, double confidence, string sourceRef, ConfirmationBasis basis)
    {
        RequireApplied(person, sourceRef, basis);
        return new(mergedSpeaker, AttributionOutcome.Flagged, person, confidence, sourceRef, basis,
            $"flagged {person} @ {confidence:0.###}");
    }

    public static AttributionVerdict OpenPoint(string mergedSpeaker, double confidence, string reason) =>
        new(mergedSpeaker, AttributionOutcome.OpenPoint, null, confidence, null, null, reason);

    public static AttributionVerdict Omitted(string mergedSpeaker, string reason) =>
        new(mergedSpeaker, AttributionOutcome.Omitted, null, 0.0, null, null, reason);

    /// <summary>
    /// A recording-LOCAL "Unknown speaker N" label (enroll-based attribution): no enrolled match and no
    /// unambiguous participant hint, so the voice is left unidentified but given a deterministic local
    /// handle (stable per <c>(recordingId, clusterIndex)</c>) and FLAGGED. It writes NO name to
    /// <c>map/</c> (an <see cref="AttributionOutcome.Omitted"/> outcome — the never-fabricate floor); the
    /// label is carried in <see cref="LocalUnknownLabel"/> for the transcript's local rendering. There is
    /// NO cross-recording linking — the label is meaningful only within this recording.
    /// </summary>
    public static AttributionVerdict UnknownLocal(string mergedSpeaker, string localLabel)
    {
        if (string.IsNullOrWhiteSpace(localLabel))
            throw new ArgumentException("UnknownLocal requires a non-empty local label", nameof(localLabel));
        return new(mergedSpeaker, AttributionOutcome.Omitted, null, 0.0, null, null,
            $"unknown speaker (no enrolled match, no unambiguous participant hint) — local label '{localLabel}'")
        { LocalUnknownLabel = localLabel };
    }

    /// <summary>
    /// The recording-LOCAL "Unknown speaker N" label, present ONLY for an <see cref="UnknownLocal"/>
    /// verdict (else null). Deterministic per <c>(recordingId, clusterIndex)</c>; never cross-recording.
    /// </summary>
    public string? LocalUnknownLabel { get; init; }

    private static void RequireApplied(string person, string sourceRef, ConfirmationBasis basis)
    {
        if (string.IsNullOrWhiteSpace(person))
            throw new ArgumentException("an applied attribution must name a person", nameof(person));
        if (string.IsNullOrWhiteSpace(sourceRef))
            throw new ArgumentException("an applied attribution must carry a source ref (lint R9)", nameof(sourceRef));
        ArgumentNullException.ThrowIfNull(basis);
    }
}
