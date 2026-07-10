namespace Cervello.Enrichment.Ports;

/// <summary>
/// The DURABLE, runtime-mutable extension of the §10 enrollment allowlist (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §6.4 + §9 fork 1, decision "rename auto-adds to the §10
/// allowlist"). The static <see cref="Cervello.Enrichment.Domain.EnrollmentAllowlist"/> is DATA
/// injected at deploy (who consented out-of-band); this store is the SECOND consent source the V5
/// rename-poller writes to: when the operator renames <c>unknown_03 → Marco</c>, that rename IS the
/// operator asserting consent to enroll <c>marco</c> (an operator-ratified decision), so the poller
/// adds <c>marco</c> here BEFORE the enroll write.
///
/// <para><b>Why a separate store, not a wider static allowlist.</b> The static allowlist is an
/// immutable value object (a deploy-time snapshot); a Drive rename is a RUNTIME event that must add a
/// person durably without a redeploy. This store is that durable, additive consent ledger — its slugs
/// UNION with the static allowlist inside the voiceprint store's §10 gate, so the gate stays a single
/// authoritative check and nothing enrolls off both sources.</para>
///
/// <para>Confinement: this holds only person SLUGS + a consent timestamp — non-biometric metadata,
/// never a centroid. CT146-side alongside the other cervello state; the audit trail (who consented,
/// when, via which rename) is the access log the poller also writes.</para>
/// </summary>
public interface IEnrollmentConsentStore
{
    /// <summary>
    /// Record that <paramref name="personSlug"/> has consented to enrollment (idempotent — re-adding
    /// an already-consented slug is a harmless no-op). <paramref name="basisId"/> is the
    /// <c>human://rename:&lt;fileId&gt;</c> basis that authorised it (audit only). Returns true if this
    /// call newly added the consent, false if it was already present.
    /// </summary>
    Task<bool> AddConsentAsync(string personSlug, string basisId, CancellationToken ct = default);

    /// <summary>Whether <paramref name="personSlug"/> has a durable rename-consent record.</summary>
    Task<bool> IsConsentedAsync(string personSlug, CancellationToken ct = default);
}
