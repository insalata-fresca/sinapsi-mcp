namespace Cervello.Enrichment.Domain;

/// <summary>
/// The output of the enroll-based attribution stage for one recording: the per-cluster
/// <see cref="AttributionVerdict"/>s (enrolled / participant-hint / unknown / ambiguous) plus any
/// auto-ENROLLMENT PROPOSALS a participant-hint assignment produced. The proposals are PROPOSALS only —
/// the actual enroll WRITE stays behind the escalate-only apply gate (M4 ships dark; the flip is M6).
/// </summary>
public sealed record AttributionResult
{
    public AttributionResult(
        IReadOnlyList<AttributionVerdict> verdicts,
        IReadOnlyList<EnrollmentProposal> enrollmentProposals)
    {
        Verdicts = verdicts ?? throw new ArgumentNullException(nameof(verdicts));
        EnrollmentProposals = enrollmentProposals ?? throw new ArgumentNullException(nameof(enrollmentProposals));
    }

    /// <summary>One verdict per merged (within-recording) voice cluster.</summary>
    public IReadOnlyList<AttributionVerdict> Verdicts { get; }

    /// <summary>Auto-enrollment proposals from participant-hint assignments (proposals only — M4 dark).</summary>
    public IReadOnlyList<EnrollmentProposal> EnrollmentProposals { get; }

    public static AttributionResult Empty { get; } =
        new(Array.Empty<AttributionVerdict>(), Array.Empty<EnrollmentProposal>());
}

/// <summary>
/// A PROPOSED auto-enrollment of a person's voiceprint from a recording's within-recording voice
/// cluster (design: participant-hint assignment). The enroll WRITE is NOT performed here — it stays
/// behind the escalate-only apply gate (M6). The proposal carries the person slug, the recording +
/// merged-cluster label the voice came from, and the centroid to enroll from.
/// </summary>
public sealed record EnrollmentProposal
{
    public EnrollmentProposal(
        string personSlug, string recordingId, string mergedSpeaker, IReadOnlyList<float> centroid)
    {
        if (string.IsNullOrWhiteSpace(personSlug))
            throw new ArgumentException("EnrollmentProposal.PersonSlug must be non-empty", nameof(personSlug));
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("EnrollmentProposal.RecordingId must be non-empty", nameof(recordingId));
        if (string.IsNullOrWhiteSpace(mergedSpeaker))
            throw new ArgumentException("EnrollmentProposal.MergedSpeaker must be non-empty", nameof(mergedSpeaker));
        ArgumentNullException.ThrowIfNull(centroid);
        PersonSlug = personSlug;
        RecordingId = recordingId;
        MergedSpeaker = mergedSpeaker;
        Centroid = centroid;
    }

    public string PersonSlug { get; }
    public string RecordingId { get; }
    public string MergedSpeaker { get; }

    /// <summary>The within-recording voice-cluster centroid to enroll the person's voiceprint from.</summary>
    public IReadOnlyList<float> Centroid { get; }

    /// <summary>The <c>rec://&lt;id&gt;#&lt;seg&gt;</c> source ref the enrollment would cite.</summary>
    public string SourceRef => $"rec://{RecordingId}#{MergedSpeaker}";
}
