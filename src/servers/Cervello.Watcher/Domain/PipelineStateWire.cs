namespace Cervello.Watcher.Domain;

/// <summary>
/// The SCHEMAS §5 wire (string) form of <see cref="PipelineState"/> — the canonical serialization
/// for the shared <c>recordings</c> state row (E4 enum reconciliation). These strings are IDENTICAL
/// to the engine's <c>EnrichmentStateMachine.Name(...)</c>, so the Watcher and the enrichment
/// engine can share state rows: the Watcher writes <c>normalized</c>, the engine reads
/// <c>normalized</c> and drives it forward to <c>enriched / bundle_created / …</c>.
///
/// <para><see cref="Parse"/> is deliberately TOLERANT: it accepts the §5 wire names AND the legacy
/// PascalCase (<c>Enum.ToString()</c>) form the pre-reconciliation Watcher persisted, so existing
/// rows load without a data migration.</para>
/// </summary>
public static class PipelineStateWire
{
    private static readonly IReadOnlyDictionary<PipelineState, string> ToWireMap =
        new Dictionary<PipelineState, string>
        {
            [PipelineState.Queued] = "queued",
            [PipelineState.Normalized] = "normalized",
            [PipelineState.Enriched] = "enriched",
            [PipelineState.AttentionScored] = "attention_scored",
            [PipelineState.BundleCreated] = "bundle_created",
            [PipelineState.GraphPrOpened] = "graph_pr_opened",
            [PipelineState.GraphMerged] = "graph_merged",
            [PipelineState.Rejected] = "rejected",
            [PipelineState.FailedRetryable] = "failed_retryable",
            [PipelineState.FailedTerminal] = "failed_terminal",
        };

    private static readonly IReadOnlyDictionary<string, PipelineState> FromWireMap =
        ToWireMap.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    /// <summary>The SCHEMAS §5 lowercase wire name for a state (e.g. <c>bundle_created</c>).</summary>
    public static string ToWire(this PipelineState state) => ToWireMap[state];

    /// <summary>
    /// Parse a persisted state string. Accepts the SCHEMAS §5 wire name first; falls back to the
    /// legacy PascalCase <c>Enum.ToString()</c> form (so pre-reconciliation rows still load).
    /// </summary>
    public static PipelineState Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("pipeline state string must be non-empty", nameof(value));
        if (FromWireMap.TryGetValue(value, out var byWire)) return byWire;
        // Legacy tolerance: the pre-E4 Watcher persisted Enum.ToString() (PascalCase). Also maps the
        // old dead names Attributed/Graphed onto their §5 successors so no row is ever unparseable.
        return value switch
        {
            "Attributed" => PipelineState.AttentionScored,
            "Graphed" => PipelineState.GraphMerged,
            _ when Enum.TryParse<PipelineState>(value, ignoreCase: false, out var e) => e,
            _ => throw new FormatException($"unrecognised pipeline state '{value}' (not a SCHEMAS §5 wire name or legacy form)"),
        };
    }
}
