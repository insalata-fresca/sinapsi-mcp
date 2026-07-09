using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Policy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline;

/// <summary>
/// M6 item 2 — the EVIDENCE-GATED, DARK-BY-DEFAULT auto-enrollment coordinator. It turns a
/// participant-hint <see cref="EnrollmentProposal"/> into an actual voiceprint WRITE
/// (<see cref="VoiceprintEnrollment.EnrollOnConfirmationAsync"/>) — but ONLY under the conjunction of
/// three hard conditions, ALL of which must hold:
///
/// <list type="number">
///   <item><b>(a) <see cref="PolicyPhase.GradedAutoApply"/> is active.</b> Under the default
///     <see cref="PolicyPhase.EscalateOnly"/> NOTHING is written — the proposal stays a proposal
///     (logged), exactly as before M6. This is the dark-by-default gate: no phase flip, no write.</item>
///   <item><b>(b) the proposal is the unambiguous 1:1 case</b> — proven by its verdict being an
///     <see cref="AttributionOutcome.AutoApplied"/> attribution to the SAME person, carrying the
///     <see cref="ConfirmationBasis.ParticipantHintRule"/> metadata basis the policy issued. A
///     withheld (open-point) or contested hint has no such verdict → no write. The verdict is the
///     confirmation the enroll write is tied to (never a raw match).</item>
///   <item><b>(c) the person passes the §10 <see cref="EnrollmentAllowlist"/>.</b> The allowlist is
///     enforced inside the store, which THROWS <see cref="EnrollmentNotAllowedException"/> for an
///     off-allowlist person; the coordinator pre-checks it AND catches the throw so an off-allowlist
///     enroll is REFUSED (logged, skipped) — never a silent success and never a drain failure.</item>
/// </list>
///
/// <para>Enrolls the CORRECT voiceprint: the unmatched voice cluster's centroid
/// (<see cref="EnrollmentProposal.Centroid"/>), tied to the correct hinted person
/// (<see cref="EnrollmentProposal.PersonSlug"/>), with the <c>rec://</c> source ref
/// (<see cref="EnrollmentProposal.SourceRef"/>) and the auto-apply verdict's basis as the confirmation.</para>
///
/// <para>Pure gate + a single store write per admitted proposal — no network, no NATS. A failed write
/// (store error, off-allowlist) is caught per-proposal and never propagates: auto-enroll is an additive
/// enhancement, never a reason to fail a drain that already produced a valid attribution.</para>
/// </summary>
public sealed class AutoEnrollmentCoordinator(
    VoiceprintEnrollment enrollment,
    EnrollmentAllowlist allowlist,
    PolicyPhase phase,
    ILogger<AutoEnrollmentCoordinator>? logger = null)
{
    private readonly VoiceprintEnrollment _enrollment =
        enrollment ?? throw new ArgumentNullException(nameof(enrollment));
    private readonly EnrollmentAllowlist _allowlist =
        allowlist ?? throw new ArgumentNullException(nameof(allowlist));
    private readonly PolicyPhase _phase = phase;
    private readonly ILogger _log = logger ?? NullLogger<AutoEnrollmentCoordinator>.Instance;

    /// <summary>Whether auto-enroll is even POSSIBLE this run (the dark-by-default gate — condition (a)).</summary>
    public bool Enabled => _phase == PolicyPhase.GradedAutoApply;

    /// <summary>
    /// Apply the auto-enroll gate to a recording's attribution result, writing a voiceprint for each
    /// proposal that clears (a)+(b)+(c). Returns the persons actually enrolled (for the audit line).
    /// Under <see cref="PolicyPhase.EscalateOnly"/> it writes NOTHING and returns an empty list.
    /// </summary>
    public async Task<IReadOnlyList<string>> AutoEnrollAsync(
        string recordingId, AttributionResult attribution, DateOnly on, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new ArgumentException("recordingId must be non-empty", nameof(recordingId));
        ArgumentNullException.ThrowIfNull(attribution);

        // (a) DARK BY DEFAULT: under escalate-only nothing is ever written. The proposals remain
        //     proposals (the caller logs them). This is the guarantee M6 ships with the flag OFF.
        if (!Enabled)
            return Array.Empty<string>();

        if (attribution.EnrollmentProposals.Count == 0)
            return Array.Empty<string>();

        // Index the applied participant-hint verdicts by (person → the verdict that named them), so a
        // proposal is admitted ONLY when its person was actually AUTO-APPLIED via the participant-hint
        // basis this same run — condition (b). An open-point/contested hint has no such verdict.
        var appliedHint = attribution.Verdicts
            .Where(v => v.Outcome == AttributionOutcome.AutoApplied
                        && v.Person is not null
                        && v.Basis is { Kind: ConfirmationBasisKind.Auto, Rule: ConfirmationBasis.ParticipantHintRule })
            .GroupBy(v => v.Person!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var enrolled = new List<string>();
        foreach (var proposal in attribution.EnrollmentProposals)
        {
            // (b) the proposal's person must have an AUTO-APPLIED participant-hint verdict this run.
            if (!appliedHint.TryGetValue(proposal.PersonSlug, out var verdict))
            {
                _log.LogInformation(
                    "auto-enroll {Rec}: proposal for {Person} not written — no auto-applied participant-hint verdict (withheld/contested)",
                    recordingId, proposal.PersonSlug);
                continue;
            }

            // (c) §10 allowlist. Pre-check so a refused enroll is a logged skip, not an exception path.
            if (!_allowlist.IsAllowed(proposal.PersonSlug))
            {
                _log.LogWarning(
                    "auto-enroll {Rec}: REFUSED for {Person} — not on the §10 enrollment allowlist (voiceprint NOT written)",
                    recordingId, proposal.PersonSlug);
                continue;
            }

            try
            {
                // Enroll the CORRECT voiceprint (the unmatched voice cluster's centroid), tied to the
                // hinted person, with the auto-apply verdict's metadata basis as the confirmation.
                var result = await _enrollment.EnrollOnConfirmationAsync(
                    proposal.PersonSlug,
                    proposal.Centroid,
                    [proposal.SourceRef],
                    matchCosine: null,
                    verdict.Basis!,
                    on,
                    ct).ConfigureAwait(false);
                enrolled.Add(proposal.PersonSlug);
                _log.LogInformation(
                    "auto-enroll {Rec}: {Action} {Person} from {Source} (basis {Basis}) — GradedAutoApply + 1:1 + allowlist",
                    recordingId, result.WasRefine ? "refined" : "enrolled",
                    proposal.PersonSlug, proposal.SourceRef, verdict.Basis!.Id);
            }
            catch (EnrollmentNotAllowedException)
            {
                // Belt-and-braces: the store is the authority on the allowlist. A throw here (e.g. the
                // store's allowlist is stricter than ours) is a REFUSAL, never a success — logged, skipped.
                _log.LogWarning(
                    "auto-enroll {Rec}: REFUSED for {Person} by the store's §10 allowlist gate (voiceprint NOT written)",
                    recordingId, proposal.PersonSlug);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A store/write failure must NOT fail the drain — the attribution already stands.
                _log.LogWarning(ex,
                    "auto-enroll {Rec}: enroll of {Person} failed (non-fatal — attribution stands, drain continues)",
                    recordingId, proposal.PersonSlug);
            }
        }

        return enrolled;
    }
}
