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
/// <para><b>Bounded lifetime.</b> The mark is set at enroll time and cleared once the re-attribution
/// requeue that consumed it has been driven through the drain (V6 clears it after the requeue), so a
/// FUTURE, unrelated recording that happens to match <c>marco</c> later routes through the normal
/// policy again (the auto-apply is for THIS re-attribution pass, not forever). Confinement: person
/// slugs only, never a centroid.</para>
/// </summary>
public interface IRecentEnrollmentStore
{
    /// <summary>Mark <paramref name="personSlug"/> as just human-enrolled, carrying the enrollment's <c>human://</c> basis id (idempotent).</summary>
    Task MarkAsync(string personSlug, string humanBasisId, CancellationToken ct = default);

    /// <summary>The <c>human://</c> basis id if <paramref name="personSlug"/> is currently a just-enrolled print, else null.</summary>
    Task<string?> GetBasisAsync(string personSlug, CancellationToken ct = default);

    /// <summary>Clear the just-enrolled mark for <paramref name="personSlug"/> (after V6's requeue is driven through). No-op if absent.</summary>
    Task ClearAsync(string personSlug, CancellationToken ct = default);
}
