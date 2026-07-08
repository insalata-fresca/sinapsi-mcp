using Cervello.Watcher.Domain;
using Xunit;

namespace Cervello.Watcher.Tests;

/// <summary>
/// E4 enum reconciliation: the Watcher's <see cref="PipelineState"/> now serializes to the exact
/// SCHEMAS §5 wire names the engine's <c>EnrichmentState</c> uses, so the two modules can SHARE the
/// <c>recordings</c> state row. These tests pin the wire mapping (the shared contract) and the
/// tolerant parse (so pre-E4 PascalCase rows still load without a migration).
/// </summary>
public sealed class PipelineStateReconciliationTests
{
    [Theory]
    [InlineData(PipelineState.Queued, "queued")]
    [InlineData(PipelineState.Normalized, "normalized")]
    [InlineData(PipelineState.Enriched, "enriched")]
    [InlineData(PipelineState.AttentionScored, "attention_scored")]
    [InlineData(PipelineState.BundleCreated, "bundle_created")]
    [InlineData(PipelineState.GraphPrOpened, "graph_pr_opened")]
    [InlineData(PipelineState.GraphMerged, "graph_merged")]
    [InlineData(PipelineState.Rejected, "rejected")]
    [InlineData(PipelineState.FailedRetryable, "failed_retryable")]
    [InlineData(PipelineState.FailedTerminal, "failed_terminal")]
    public void Wire_names_match_SCHEMAS_section_5(PipelineState state, string expected)
    {
        Assert.Equal(expected, state.ToWire());
        Assert.Equal(state, PipelineStateWire.Parse(expected)); // round-trips
    }

    [Fact]
    public void Parse_tolerates_legacy_pascalcase_rows_no_migration()
    {
        // Pre-E4 the Watcher persisted Enum.ToString() (PascalCase). Those rows must still load.
        Assert.Equal(PipelineState.Normalized, PipelineStateWire.Parse("Normalized"));
        Assert.Equal(PipelineState.Queued, PipelineStateWire.Parse("Queued"));
        Assert.Equal(PipelineState.FailedRetryable, PipelineStateWire.Parse("FailedRetryable"));
        // The old dead names map onto their §5 successors so no row is ever unparseable.
        Assert.Equal(PipelineState.AttentionScored, PipelineStateWire.Parse("Attributed"));
        Assert.Equal(PipelineState.GraphMerged, PipelineStateWire.Parse("Graphed"));
    }

    [Fact]
    public void Unknown_state_is_rejected_never_invented()
    {
        Assert.Throws<FormatException>(() => PipelineStateWire.Parse("teleported"));
        Assert.Throws<ArgumentException>(() => PipelineStateWire.Parse(""));
    }
}
