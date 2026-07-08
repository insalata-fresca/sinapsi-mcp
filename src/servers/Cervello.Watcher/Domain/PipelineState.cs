namespace Cervello.Watcher.Domain;

/// <summary>
/// Pipeline state for a download item / recording (SCHEMAS §5). This module (WATCH → NORMALIZE)
/// only ever DRIVES a row to <see cref="Queued"/> / <see cref="Normalized"/> (plus the two failure
/// sinks); the later spine stages are declared here so the shared <c>recordings</c> state row and
/// this enum stay aligned with the engine's <c>Cervello.Enrichment.Domain.EnrichmentState</c> —
/// they now use the SAME member set and the SAME SCHEMAS §5 WIRE NAMES (E4 enum reconciliation).
///
/// <para><b>Enum reconciliation (E4).</b> The earlier draft declared the later stages as
/// <c>Enriched / Attributed / Graphed</c>, which did NOT match the SCHEMAS §5 wire names
/// (<c>attention_scored / bundle_created / graph_pr_opened / graph_merged</c>). Those members were
/// DEAD (the Watcher never reaches them), so they are renamed to the §5 set here with no behaviour
/// change. Persistence now uses <see cref="PipelineStateWire.ToWire"/> (the §5 lowercase strings),
/// and the read side (<see cref="PipelineStateWire.Parse"/>) tolerantly accepts BOTH the §5 wire
/// names and the legacy PascalCase strings, so existing rows load without a migration.</para>
/// </summary>
public enum PipelineState
{
    /// <summary>Change seen / download recorded, not yet normalized.</summary>
    Queued,

    /// <summary>Paired + registered in the manifest with an entry (the Watcher's terminal stage).</summary>
    Normalized,

    // ---- later spine stages (SCHEMAS §5; declared for a stable shared row, driven by the engine) ----
    Enriched,
    AttentionScored,
    BundleCreated,
    GraphPrOpened,
    GraphMerged,

    /// <summary>Operator/engine rejected the bundle (reason-bearing sink, SCHEMAS §5).</summary>
    Rejected,

    /// <summary>A transient error (5xx / timeout / proxy) — retried under the same key.</summary>
    FailedRetryable,

    /// <summary>A non-recoverable error (404 / malformed / non-audio) — carries a reason.</summary>
    FailedTerminal,
}
