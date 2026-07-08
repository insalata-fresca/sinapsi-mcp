using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Pipeline.Stages;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// enrichment-linking — "Ingest a normalized recording idempotently". A normalized+ready
/// recording is picked up exactly once (keyed by rec:&lt;id&gt;:&lt;sha&gt;); a replay is a
/// no-op; states advance only forward.
/// </summary>
public sealed class IngestStageTests
{
    private static RecordingRef Ready() => new("20260601-guilhem", "sha-aaa", "m4a", "fr", ready: true);

    // ---- Scenario: A normalized recording is picked up once ----
    [Fact]
    public async Task Normalized_ready_recording_is_picked_up_exactly_once()
    {
        var ledger = new InMemoryEnrichmentLedger();
        var stage = new IngestStage(ledger);
        var rec = Ready();

        var first = await stage.IngestAsync(rec, EnrichmentState.Normalized);
        Assert.True(first.PickedUp);
        Assert.False(first.IsReplay);
        Assert.Equal(EnrichmentState.Enriched, first.State); // advanced forward

        // A second trigger for the same rec: key is a logged no-op.
        var second = await stage.IngestAsync(rec, EnrichmentState.Normalized);
        Assert.False(second.PickedUp);
        Assert.True(second.IsReplay);
    }

    // ---- The idempotency key is rec:<id>:<audio-sha256> (SCHEMAS §5) ----
    [Fact]
    public void Idempotency_key_is_rec_id_audioSha()
    {
        Assert.Equal("rec:20260601-guilhem:sha-aaa", Ready().IdempotencyKey);
    }

    // ---- Not eligible: a recording not yet normalized is skipped (no claim) ----
    [Fact]
    public async Task A_queued_recording_is_not_eligible()
    {
        var ledger = new InMemoryEnrichmentLedger();
        var stage = new IngestStage(ledger);

        var result = await stage.IngestAsync(Ready(), EnrichmentState.Queued);

        Assert.False(result.PickedUp);
        Assert.False(result.IsReplay);
        Assert.False(await ledger.IsClaimedAsync(Ready().IdempotencyKey)); // nothing claimed
    }

    // ---- Not eligible: normalized but the ready marker isn't set ----
    [Fact]
    public async Task A_normalized_recording_without_the_ready_marker_is_not_picked_up()
    {
        var ledger = new InMemoryEnrichmentLedger();
        var stage = new IngestStage(ledger);
        var notReady = new RecordingRef("id", "sha", "m4a", "fr", ready: false);

        var result = await stage.IngestAsync(notReady, EnrichmentState.Normalized);

        Assert.False(result.PickedUp);
        Assert.Contains("ready", result.Reason);
        Assert.False(await ledger.IsClaimedAsync(notReady.IdempotencyKey));
    }

    // ---- Two distinct recordings each get picked up (keys are independent) ----
    [Fact]
    public async Task Distinct_recordings_are_each_picked_up()
    {
        var ledger = new InMemoryEnrichmentLedger();
        var stage = new IngestStage(ledger);

        var a = await stage.IngestAsync(new RecordingRef("a", "sa", "m4a", "fr", true), EnrichmentState.Normalized);
        var b = await stage.IngestAsync(new RecordingRef("b", "sb", "m4a", "fr", true), EnrichmentState.Normalized);

        Assert.True(a.PickedUp);
        Assert.True(b.PickedUp);
    }
}
