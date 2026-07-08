using Cervello.Enrichment.Domain;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// enrichment-linking — "States advance only forward" (SCHEMAS §5). Verifies the state names,
/// forward-only transitions, the failed_retryable → queued retry edge, terminal-sink behaviour,
/// and the reason-required rule for rejected / failed_terminal.
/// </summary>
public sealed class EnrichmentStateTests
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
    public void State_wire_names_match_schemas_section5(EnrichmentState s, string wire)
    {
        Assert.Equal(wire, EnrichmentStateMachine.Name(s));
    }

    [Fact]
    public void Forward_spine_transitions_are_legal()
    {
        Assert.True(EnrichmentStateMachine.IsLegalTransition(EnrichmentState.Normalized, EnrichmentState.Enriched));
        Assert.True(EnrichmentStateMachine.IsLegalTransition(EnrichmentState.Enriched, EnrichmentState.AttentionScored));
        Assert.True(EnrichmentStateMachine.IsLegalTransition(EnrichmentState.BundleCreated, EnrichmentState.GraphPrOpened));
        Assert.True(EnrichmentStateMachine.IsLegalTransition(EnrichmentState.GraphPrOpened, EnrichmentState.GraphMerged));
    }

    [Fact]
    public void Backward_transitions_are_illegal()
    {
        Assert.False(EnrichmentStateMachine.IsLegalTransition(EnrichmentState.Enriched, EnrichmentState.Normalized));
        Assert.False(EnrichmentStateMachine.IsLegalTransition(EnrichmentState.GraphMerged, EnrichmentState.Enriched));
    }

    [Fact]
    public void Failed_retryable_returns_only_to_queued()
    {
        Assert.True(EnrichmentStateMachine.IsLegalTransition(EnrichmentState.FailedRetryable, EnrichmentState.Queued));
        Assert.False(EnrichmentStateMachine.IsLegalTransition(EnrichmentState.FailedRetryable, EnrichmentState.Enriched));
    }

    [Fact]
    public void Rejected_and_failed_terminal_are_sinks()
    {
        Assert.False(EnrichmentStateMachine.IsLegalTransition(EnrichmentState.Rejected, EnrichmentState.Queued));
        Assert.False(EnrichmentStateMachine.IsLegalTransition(EnrichmentState.FailedTerminal, EnrichmentState.Queued));
    }

    [Fact]
    public void Any_live_state_can_fail_out()
    {
        Assert.True(EnrichmentStateMachine.IsLegalTransition(EnrichmentState.Enriched, EnrichmentState.FailedRetryable));
        Assert.True(EnrichmentStateMachine.IsLegalTransition(EnrichmentState.AttentionScored, EnrichmentState.FailedTerminal));
    }

    [Fact]
    public void Terminal_states_require_a_reason()
    {
        Assert.Throws<InvalidOperationException>(() =>
            EnrichmentStateMachine.EnsureLegal(EnrichmentState.Enriched, EnrichmentState.FailedTerminal, reason: null));
        Assert.Throws<InvalidOperationException>(() =>
            EnrichmentStateMachine.EnsureLegal(EnrichmentState.GraphPrOpened, EnrichmentState.Rejected, reason: ""));
        // With a reason, it's fine.
        EnrichmentStateMachine.EnsureLegal(EnrichmentState.Enriched, EnrichmentState.FailedTerminal, "undecodable audio");
    }

    [Fact]
    public void EnsureLegal_throws_on_an_illegal_transition()
    {
        Assert.Throws<InvalidOperationException>(() =>
            EnrichmentStateMachine.EnsureLegal(EnrichmentState.GraphMerged, EnrichmentState.Enriched));
    }
}
