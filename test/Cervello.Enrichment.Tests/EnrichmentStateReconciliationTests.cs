using Cervello.Enrichment.Domain;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// E4 enum reconciliation (engine side): the engine's <see cref="EnrichmentState"/> serializes to
/// the SCHEMAS §5 wire names AND can READ the wire string the Watcher writes — the bridge that lets
/// them share the <c>recordings</c> state row. The Watcher-side mapping is pinned in
/// <c>PipelineStateReconciliationTests</c> (Watcher assembly); the wire strings asserted here are
/// the SAME contract, so the two modules agree by construction.
/// </summary>
public sealed class EnrichmentStateReconciliationTests
{
    [Theory]
    [InlineData(EnrichmentState.Queued, "queued")]
    [InlineData(EnrichmentState.Normalized, "normalized")]
    [InlineData(EnrichmentState.Enriched, "enriched")]
    [InlineData(EnrichmentState.AttentionScored, "attention_scored")]
    [InlineData(EnrichmentState.BundleCreated, "bundle_created")]
    [InlineData(EnrichmentState.GraphPrOpened, "graph_pr_opened")]
    [InlineData(EnrichmentState.GraphMerged, "graph_merged")]
    [InlineData(EnrichmentState.Rejected, "rejected")]
    [InlineData(EnrichmentState.FailedRetryable, "failed_retryable")]
    [InlineData(EnrichmentState.FailedTerminal, "failed_terminal")]
    public void Wire_names_match_SCHEMAS_section_5(EnrichmentState state, string expected)
    {
        Assert.Equal(expected, EnrichmentStateMachine.Name(state));
    }

    [Fact]
    public void Engine_reads_the_wire_string_the_watcher_wrote()
    {
        // The Watcher writes "normalized"; the engine parses it and picks the recording up.
        Assert.True(EnrichmentStateMachine.TryParse("normalized", out var s));
        Assert.Equal(EnrichmentState.Normalized, s);

        Assert.True(EnrichmentStateMachine.TryParse("bundle_created", out var b));
        Assert.Equal(EnrichmentState.BundleCreated, b);
    }

    [Fact]
    public void An_unknown_wire_string_is_never_parsed_into_an_invented_state()
    {
        Assert.False(EnrichmentStateMachine.TryParse("Normalized", out _)); // PascalCase is NOT the engine wire form
        Assert.False(EnrichmentStateMachine.TryParse("teleported", out _));
        Assert.False(EnrichmentStateMachine.TryParse("", out _));
    }
}
