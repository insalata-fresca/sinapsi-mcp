using Cervello.Enrichment.Domain;

namespace Cervello.Enrichment.Host.Drain;

/// <summary>
/// The drain-source SEAM: the enrichment host's read-side view of the recordings that the
/// M6 <c>Cervello.Watcher</c> has driven to <c>normalized</c> (SCHEMAS §5). One call returns a
/// bounded batch of eligible recordings — those whose shared <c>recordings</c> state row reads
/// <c>normalized</c> (the Watcher's terminal stage; the engine's first eligible stage).
///
/// <para><b>Handoff contract (E-HOST → M6).</b> The Watcher persists <c>watcher_recording</c> rows
/// carrying <c>state = 'normalized'</c> (SCHEMAS §5 wire name, via
/// <c>Cervello.Watcher.Domain.PipelineStateWire</c>). This queue is a READ-ONLY, ADDITIVE view over
/// exactly those rows — it introduces no new write path and needs NO watcher-side change: the
/// Watcher already writes the <c>normalized</c> signal this drains. The host claims each item via
/// the engine's <c>IEnrichmentLedger</c> (idempotency key <c>rec:&lt;id&gt;:&lt;audio-sha256&gt;</c>,
/// §8) before running any stage, so a replay of a seen key is a no-op — the queue itself is NOT
/// required to be exactly-once.</para>
///
/// <para>The live adapter (<see cref="PgNormalizedWorkQueue"/>) reads the Watcher's table; the fake
/// (<see cref="InMemoryNormalizedWorkQueue"/>) drives the drain loop offline in tests.</para>
/// </summary>
public interface INormalizedWorkQueue
{
    /// <summary>
    /// Return up to <paramref name="max"/> recordings currently in <c>normalized</c>, oldest first.
    /// A recording is returned as a <see cref="RecordingRef"/> (the minimal handle the engine's
    /// <c>IngestStage</c> needs). Never returns an item already advanced past <c>normalized</c>.
    /// </summary>
    Task<IReadOnlyList<RecordingRef>> LeaseNormalizedAsync(int max, CancellationToken ct = default);

    /// <summary>
    /// Persist a recording's advanced <see cref="EnrichmentState"/> back to the shared state row
    /// after a drain pass (e.g. <c>normalized → enriched</c>, or a failure sink). Idempotent: writing
    /// the same state twice is harmless. The state is serialized as its SCHEMAS §5 wire name so the
    /// Watcher and the engine keep sharing the row (E4 enum reconciliation).
    /// </summary>
    Task AdvanceStateAsync(RecordingRef recording, EnrichmentState state, string? reason, CancellationToken ct = default);
}
