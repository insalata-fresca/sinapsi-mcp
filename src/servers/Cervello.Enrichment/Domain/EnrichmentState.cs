namespace Cervello.Enrichment.Domain;

/// <summary>
/// The pipeline state machine exactly as SCHEMAS §5 declares it:
/// <c>queued → normalized → enriched → attention_scored → bundle_created →
/// graph_pr_opened → graph_merged | rejected | failed_retryable | failed_terminal</c>.
///
/// <para>The enrichment engine picks a recording up at <see cref="Normalized"/> and drives
/// it forward. This enum is the engine-side source of truth for §5; the wire names come from
/// <see cref="StateNames"/> (the string forms SCHEMAS §5 / §8 use). Legal transitions are
/// forward-only, plus <c>failed_retryable → queued</c> (retry with the same idempotency key).</para>
///
/// <para>NOTE (spec-divergence flagged for MC — see mission return): the WATCH-side
/// <c>Cervello.Watcher.Domain.PipelineState</c> enum declares its later stages as
/// <c>Enriched / Attributed / Graphed</c>, which do NOT match the SCHEMAS §5 string names
/// (<c>attention_scored / bundle_created / graph_pr_opened / graph_merged</c>). The two enums
/// are not unified here (separate modules; the Watcher only ever reaches <c>normalized</c>).
/// The engine follows SCHEMAS §5 verbatim, since §5 is the normative machine-checked grammar.</para>
/// </summary>
public enum EnrichmentState
{
    Queued,
    Normalized,
    Enriched,
    AttentionScored,
    BundleCreated,
    GraphPrOpened,
    GraphMerged,
    Rejected,
    FailedRetryable,
    FailedTerminal,
}

/// <summary>The SCHEMAS §5 / §8 wire (string) names for each state, and the transition rules.</summary>
public static class EnrichmentStateMachine
{
    private static readonly IReadOnlyDictionary<EnrichmentState, string> Names =
        new Dictionary<EnrichmentState, string>
        {
            [EnrichmentState.Queued] = "queued",
            [EnrichmentState.Normalized] = "normalized",
            [EnrichmentState.Enriched] = "enriched",
            [EnrichmentState.AttentionScored] = "attention_scored",
            [EnrichmentState.BundleCreated] = "bundle_created",
            [EnrichmentState.GraphPrOpened] = "graph_pr_opened",
            [EnrichmentState.GraphMerged] = "graph_merged",
            [EnrichmentState.Rejected] = "rejected",
            [EnrichmentState.FailedRetryable] = "failed_retryable",
            [EnrichmentState.FailedTerminal] = "failed_terminal",
        };

    // The linear forward spine (SCHEMAS §5), used to reject any backward jump.
    private static readonly EnrichmentState[] Spine =
    [
        EnrichmentState.Queued,
        EnrichmentState.Normalized,
        EnrichmentState.Enriched,
        EnrichmentState.AttentionScored,
        EnrichmentState.BundleCreated,
        EnrichmentState.GraphPrOpened,
        EnrichmentState.GraphMerged,
    ];

    /// <summary>The SCHEMAS §5 / §8 string form of a state.</summary>
    public static string Name(EnrichmentState state) => Names[state];

    private static bool IsTerminalFailure(EnrichmentState s) =>
        s is EnrichmentState.Rejected or EnrichmentState.FailedTerminal;

    /// <summary>Whether <paramref name="to"/> is a legal transition from <paramref name="from"/> (SCHEMAS §5).</summary>
    public static bool IsLegalTransition(EnrichmentState from, EnrichmentState to)
    {
        if (from == to) return false; // a transition must move
        if (IsTerminalFailure(from)) return false; // rejected / failed_terminal are sinks

        // failed_retryable → queued (retry with the same idempotency key)
        if (from == EnrichmentState.FailedRetryable) return to == EnrichmentState.Queued;

        // From any live spine state we may fail out at any point.
        if (to is EnrichmentState.FailedRetryable or EnrichmentState.FailedTerminal or EnrichmentState.Rejected)
            return true;

        // Otherwise only forward along the spine (strictly, by one or more).
        var fi = Array.IndexOf(Spine, from);
        var ti = Array.IndexOf(Spine, to);
        return fi >= 0 && ti > fi;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if the transition is illegal (backward /
    /// invented / out of a sink). <paramref name="reason"/> is required for the reason-bearing
    /// terminal states (SCHEMAS §5).
    /// </summary>
    public static void EnsureLegal(EnrichmentState from, EnrichmentState to, string? reason = null)
    {
        if (!IsLegalTransition(from, to))
            throw new InvalidOperationException(
                $"illegal pipeline transition {Name(from)} → {Name(to)} (SCHEMAS §5: forward-only, " +
                $"plus failed_retryable → queued)");

        if (to is EnrichmentState.Rejected or EnrichmentState.FailedTerminal
            && string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException(
                $"transition to {Name(to)} requires a reason string (SCHEMAS §5)");
    }
}
