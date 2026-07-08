using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for the CT146 pgvector voiceprint store (spec <c>voiceprint-store</c>). Enrolled
/// centroids live only here (never git, never a shared subject, never off CT146). The live
/// adapter is <c>PgVoiceprintStore</c> (E3 CT146); tests + the offline slice use an in-memory
/// implementation with identical contract.
///
/// <para>Every mutation that WRITES a centroid (<see cref="EnrollOrRefineAsync"/>) is gated by
/// the §10 enrollment allowlist inside the implementation; <see cref="DeleteAsync"/> implements
/// the OPERATIONS §7 deletion runbook (remove centroid + enrollment rows + audio; the caller
/// re-marks prior attributions + sets <c>voice: deleted</c>).</para>
/// </summary>
public interface IVoiceprintStore
{
    /// <summary>
    /// Cosine-match a merged cluster centroid against all enrolled voiceprints, returning matches
    /// ordered by descending cosine. Returns an empty list when nothing is enrolled. Matching is a
    /// READ — it does not touch <c>last_match</c> (only a confirmed enroll/refine does).
    /// </summary>
    Task<IReadOnlyList<VoiceprintMatch>> MatchAsync(IReadOnlyList<float> centroid, CancellationToken ct = default);

    /// <summary>Get a person's enrolled voiceprint, or null if not enrolled.</summary>
    Task<Voiceprint?> GetAsync(string personSlug, CancellationToken ct = default);

    /// <summary>
    /// Enroll a new voiceprint or refine the existing one from a person's CONFIRMED segments
    /// (running centroid: increment <c>sample_count</c>, update the centroid, set
    /// <c>last_match</c>). PERMITTED ONLY for a person on the §10 allowlist — otherwise throws
    /// <see cref="EnrollmentNotAllowedException"/>. MUST be called only on a confirmation
    /// (operator answer, or a P5 auto-apply the operator did not correct) — never on an
    /// unconfirmed match.
    /// </summary>
    Task<Voiceprint> EnrollOrRefineAsync(
        string personSlug,
        IReadOnlyList<float> confirmedCentroid,
        IReadOnlyList<string> sourceSegments,
        double? matchCosine,
        DateOnly on,
        CancellationToken ct = default);

    /// <summary>
    /// The OPERATIONS §7 deletion-runbook store step: delete the person's voiceprint centroid(s)
    /// + enrollment rows and purge their enrollment audio segments. Returns whether a print
    /// existed. After deletion the person has no enrolled centroid; the caller re-marks prior
    /// attributions <c>historical/non-biometric</c> and sets the dossier <c>voice: deleted</c>.
    /// </summary>
    Task<bool> DeleteAsync(string personSlug, CancellationToken ct = default);

    /// <summary>
    /// Whether a person's voiceprint was DELETED (tombstoned). A deleted person MUST NOT get a
    /// new voice-match attribution (lint R8) — the attribution stage consults this to enforce
    /// "no re-attribution after deletion".
    /// </summary>
    Task<bool> IsDeletedAsync(string personSlug, CancellationToken ct = default);
}

/// <summary>One voiceprint match result: the person and the cosine of the match.</summary>
public sealed record VoiceprintMatch
{
    public VoiceprintMatch(string personSlug, double cosine)
    {
        if (string.IsNullOrWhiteSpace(personSlug))
            throw new ArgumentException("VoiceprintMatch.PersonSlug must be non-empty", nameof(personSlug));
        PersonSlug = personSlug;
        Cosine = cosine;
    }

    public string PersonSlug { get; }
    public double Cosine { get; }
}
