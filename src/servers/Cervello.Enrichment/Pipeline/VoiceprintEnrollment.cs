using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline;

/// <summary>
/// The enroll/refine-on-confirmation loop and the deletion-runbook coordinator (spec
/// <c>voiceprint-store</c> → "Enroll / refine on confirmation, under the allowlist" +
/// "Biometric deletion semantics"; DESIGN §5.1 learning-signal, §10; OPERATIONS §7).
///
/// <para>The LEARNING SIGNAL (DESIGN §5.1): a CONFIRMED attribution — an operator answer to a
/// speaker open-point, or a P5 auto-apply the operator did not correct — enrolls or refines that
/// person's voiceprint. This coordinator is the ONLY path that writes a centroid, so an
/// unconfirmed match can never enroll. The §10 allowlist gate lives in the store and is enforced
/// on every call.</para>
/// </summary>
public sealed class VoiceprintEnrollment(IVoiceprintStore store, ILogger<VoiceprintEnrollment>? logger = null)
{
    private readonly IVoiceprintStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger _log = logger ?? NullLogger<VoiceprintEnrollment>.Instance;

    /// <summary>
    /// Enroll or refine <paramref name="personSlug"/> from their CONFIRMED segments. Returns the
    /// updated print + the dossier <c>voice:</c> line to write (SCHEMAS §2). Throws
    /// <see cref="EnrollmentNotAllowedException"/> if the person is not on the §10 allowlist.
    /// </summary>
    /// <param name="confirmation">
    /// The confirmation basis (<c>human://</c> from an answered open-point, or <c>auto://</c> from
    /// an uncorrected P5 auto-apply) — enrollment MUST be tied to a confirmation, never a raw match.
    /// </param>
    public async Task<EnrollmentResult> EnrollOnConfirmationAsync(
        string personSlug,
        IReadOnlyList<float> confirmedCentroid,
        IReadOnlyList<string> sourceSegments,
        double? matchCosine,
        ConfirmationBasis confirmation,
        DateOnly on,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        var before = await _store.GetAsync(personSlug, ct).ConfigureAwait(false);
        var print = await _store
            .EnrollOrRefineAsync(personSlug, confirmedCentroid, sourceSegments, matchCosine, on, ct)
            .ConfigureAwait(false);

        var action = before is null ? "enrolled" : "refined";
        _log.LogInformation("voiceprint {Action} {Person}: sample_count={Count} basis={Basis}",
            action, personSlug, print.SampleCount, confirmation.Id);
        return new EnrollmentResult(print, print.DossierVoiceLine(), WasRefine: before is not null);
    }

    /// <summary>
    /// Execute the OPERATIONS §7 biometric deletion runbook for <paramref name="personSlug"/>:
    /// delete centroid + enrollment rows + enrollment audio (store), and return the
    /// <c>voice: deleted &lt;date&gt;</c> dossier line + the graph-writer instruction to re-mark
    /// prior attributions <c>historical/non-biometric</c> (steps 3–5 are the caller's — the
    /// graph-writer batch PR + access-log entry). After this, lint R8 blocks any new voice-match
    /// attribution to the person.
    /// </summary>
    public async Task<DeletionResult> DeleteVoiceprintAsync(
        string personSlug, DateOnly on, CancellationToken ct = default)
    {
        var existed = await _store.DeleteAsync(personSlug, ct).ConfigureAwait(false);
        _log.LogWarning("voiceprint deleted for {Person} (existed={Existed}) — re-mark prior attributions historical/non-biometric",
            personSlug, existed);
        return new DeletionResult(
            PersonSlug: personSlug,
            PrintExisted: existed,
            DossierVoiceLine: $"deleted {on:yyyy-MM-dd}",
            RemarkPriorAttributionsHistorical: true);
    }
}

/// <summary>The result of an enroll/refine: the updated print + the dossier <c>voice:</c> line.</summary>
public sealed record EnrollmentResult(Voiceprint Print, string DossierVoiceLine, bool WasRefine);

/// <summary>
/// The result of the deletion runbook's store step: the <c>voice: deleted &lt;date&gt;</c> dossier
/// line the caller writes + the instruction to re-mark prior attributions historical/non-biometric.
/// </summary>
public sealed record DeletionResult(
    string PersonSlug,
    bool PrintExisted,
    string DossierVoiceLine,
    bool RemarkPriorAttributionsHistorical);
