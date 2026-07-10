namespace Cervello.Enrichment.Ports;

/// <summary>
/// The V6 "just-enrolled by a human" signal (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V6, §6.6 + §9 fork 2 decision "auto-apply against
/// a human-enrolled print"). When V5's rename-poller enrolls <c>marco</c> from an operator rename, it
/// marks <c>marco</c> here; the <see cref="Cervello.Enrichment.Pipeline.Stages.AttributionStage"/>
/// then treats an AUTO-BAND corpus match to <c>marco</c> as an AUTO-APPLY carrying the enrollment's
/// <c>human://</c> basis — so the re-attributed corpus SELF-LABELS instead of re-escalating 15
/// open-points, even while the global policy phase is still <c>EscalateOnly</c>.
///
/// <para><b>Why a durable signal, not a policy-phase flip.</b> Flipping the global
/// <c>GradedAutoApply</c> phase would auto-apply EVERY attribution (the dark-by-default gate the
/// design keeps until E5 validation). This signal is SCOPED to the exact slugs the operator just
/// enrolled by rename — a bounded, operator-ratified auto-apply that never widens beyond the person
/// the operator named. Borderline (below-auto) matches STILL escalate (§9 fork 3) — this signal only
/// authorises the auto band for the just-enrolled person.</para>
///
/// <para><b>Bounded lifetime — the write-safety invariant (the load-bearing bound).</b> A mark
/// authorises auto-apply ONLY for the enrollment's OWN propagation pass, then EXPIRES by TTL.
/// <see cref="GetBasisAsync"/> returns the basis ONLY if the mark was set within a bounded window
/// (config <c>CERVELLO_RECENT_ENROLLMENT_TTL_MINUTES</c>, e.g. 3 h — long enough for the propagation
/// drain to run, far short of days/weeks). A mark OLDER than the TTL is INERT and returns null, so a
/// later unrelated ≥-auto match to that slug — INCLUDING a false-accept at the 0.62 threshold weeks
/// later — ESCALATES under escalate-only, never silently gets the <c>human://</c> basis. The TTL
/// survives restarts and needs no drain-completion coupling (which is out of this component's scope),
/// so it is the authoritative expiry.</para>
///
/// <para>There is deliberately NO explicit-clear method: clearing at requeue time would defeat the
/// propagation (the drain re-run that consumes the mark is asynchronous + later), and clearing at
/// drain-completion belongs to a future DrainWorker hook, not this signal. The TTL is the whole bound.
/// Confinement: person slugs + a timestamp only, never a centroid.</para>
/// </summary>
public interface IRecentEnrollmentStore
{
    /// <summary>Mark <paramref name="personSlug"/> as just human-enrolled (timestamped now), carrying the enrollment's <c>human://</c> basis id (idempotent — refreshes the timestamp).</summary>
    Task MarkAsync(string personSlug, string humanBasisId, CancellationToken ct = default);

    /// <summary>
    /// The <c>human://</c> basis id if <paramref name="personSlug"/> is a just-enrolled print whose mark
    /// is STILL WITHIN the TTL window, else null. A mark older than the TTL (or absent) returns null —
    /// it MUST NOT authorise an auto-apply (the write-safety bound).
    /// </summary>
    Task<string?> GetBasisAsync(string personSlug, CancellationToken ct = default);
}
