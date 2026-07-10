namespace Cervello.Enrichment.Ports;

/// <summary>
/// The V6 targeted re-attribution REQUEUE seam (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V6, §1.6/§6.6: "reset THOSE recordings' state to
/// <c>normalized</c> so the existing drain re-runs <c>AttributionStage</c>"). After a rename→enroll,
/// V6 identifies the recordings whose corpus centroids match the new print and resets ONLY those to
/// <c>normalized</c> — the same terminal state the Watcher writes for a fresh recording — so the
/// existing <c>DrainWorker</c> re-runs the full pipeline (now the print is enrolled, the match lands).
///
/// <para><b>Targeted, never blanket.</b> Only the matching recordings are requeued (design §6.6 "Only
/// reset recordings that actually match — don't blanket-reprocess"); a non-matching recording is never
/// touched. The reset is idempotent (writing <c>normalized</c> to an already-normalized row is
/// harmless) and safe under the drain's idempotency ledger — a recording already fully enriched replays
/// through the drain's replay branch, re-running <c>AttributionStage</c> against the now-enrolled print.</para>
/// </summary>
public interface IRecordingRequeue
{
    /// <summary>
    /// Reset <paramref name="recordingId"/>'s shared <c>watcher_recording</c> state to <c>normalized</c>
    /// so the drain re-runs it. Returns true if a row was reset, false if the recording is unknown.
    /// </summary>
    Task<bool> RequeueForReattributionAsync(string recordingId, CancellationToken ct = default);
}
