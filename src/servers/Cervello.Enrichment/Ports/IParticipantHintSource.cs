namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for an optional per-recording PARTICIPANT HINT — an ordered list of person names/slugs a
/// recording is believed to involve (design: enroll-based attribution, participant-hint assignment).
/// It is a HINT, never a truth: attribution uses it only for an UNAMBIGUOUS 1:1 assignment (exactly
/// one unmatched voice + exactly one hinted participant not already accounted for by an enrolled voice
/// match), and even then it PROPOSES the person + an auto-enrollment — the actual enroll WRITE stays
/// behind the escalate-only apply gate (M4 ships dark; the flip is M6). It never fabricates a name and
/// never links voices across recordings.
///
/// <para>v1 ships a simple filename-parse implementation (<c>FilenameParticipantHintSource</c>); a
/// calendar / operator-note source is an additive future implementation behind the same seam. The
/// default is EMPTY (no hint) — the worst-case plain transcript with no hint + no enrolled match still
/// works, labelling voices "Unknown speaker N" locally.</para>
/// </summary>
public interface IParticipantHintSource
{
    /// <summary>
    /// The ordered participant hint slugs for a recording (may be empty). Order is preserved so a
    /// deterministic 1:1 assignment can pick "the" unaccounted participant reproducibly.
    /// </summary>
    Task<IReadOnlyList<string>> GetParticipantsAsync(string recordingId, CancellationToken ct = default);
}
