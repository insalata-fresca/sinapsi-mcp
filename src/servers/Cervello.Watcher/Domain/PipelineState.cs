namespace Cervello.Watcher.Domain;

/// <summary>
/// Pipeline state for a download item / recording (SCHEMAS §5/§7). This change
/// (WATCH → NORMALIZE) reaches <see cref="Normalized"/>; the later stages
/// (ENRICH, ATTRIBUTE, GRAPH) are declared here so the enum is stable across the
/// five-stage spine and downstream rows never need a migration to add a value.
/// </summary>
public enum PipelineState
{
    /// <summary>Change seen / download recorded, not yet normalized.</summary>
    Queued,

    /// <summary>Paired + registered in the manifest with an entry.</summary>
    Normalized,

    /// <summary>A transient error (5xx / timeout / proxy) — retried under the same key.</summary>
    FailedRetryable,

    /// <summary>A non-recoverable error (404 / malformed / non-audio) — carries a reason.</summary>
    FailedTerminal,

    // ---- later spine stages (declared for a stable enum; not driven by this change) ----
    Enriched,
    Attributed,
    Graphed,
}
