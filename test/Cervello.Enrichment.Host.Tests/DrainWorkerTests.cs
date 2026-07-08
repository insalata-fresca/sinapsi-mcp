using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Host.Drain;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cervello.Enrichment.Host.Tests;

/// <summary>
/// The drain-loop behavioural contract (E-HOST), all against FAKES — no DB, no network, no real
/// audio. A single deterministic <c>RunCycleAsync</c> proves: a normalized item drains through the
/// pipeline entry and advances to <c>enriched</c>; an idempotent replay is a no-op; escalate-only
/// holds (no auto-apply); a per-item error maps to <c>failed_retryable</c> without aborting the batch.
/// </summary>
public sealed class DrainWorkerTests
{
    private static RecordingRef Rec(string id = "20260601-guilhem", string sha = "sha-aaa") =>
        new(id, sha, "m4a", "fr", ready: true);

    private static HostConfig Cfg(int batch = 16) =>
        HostConfig.From(new Dictionary<string, string?> { ["CERVELLO_ENRICHMENT_BATCH_SIZE"] = batch.ToString() });

    private static DrainWorker Worker(
        HostConfig cfg, INormalizedWorkQueue queue, IEnrichmentLedger ledger) =>
        new(cfg, queue, new IngestStage(ledger), ledger, NullLogger<DrainWorker>.Instance);

    // ── drain: a normalized recording advances to enriched ─────────────────────────
    [Fact]
    public async Task A_normalized_item_drains_through_the_pipeline_and_advances_to_enriched()
    {
        var queue = new InMemoryNormalizedWorkQueue();
        var ledger = new InMemoryEnrichmentLedger();
        var rec = Rec();
        queue.SeedNormalized(rec);

        var worker = Worker(Cfg(), queue, ledger);
        await worker.RunCycleAsync(default);

        Assert.Equal(1, worker.RecordingsPickedUp);
        Assert.Equal(EnrichmentState.Enriched, queue.StateOf(rec));   // shared row advanced
        Assert.True(await ledger.IsClaimedAsync(rec.IdempotencyKey)); // §8 key claimed
    }

    // ── idempotent replay: a claimed key re-leased is a no-op ───────────────────────
    [Fact]
    public async Task Idempotent_replay_of_a_claimed_key_is_a_noop()
    {
        var queue = new InMemoryNormalizedWorkQueue();
        var ledger = new InMemoryEnrichmentLedger();
        var rec = Rec();
        // Pre-claim the key (as if a previous run already picked it up) but leave the shared row at
        // normalized (as if the advance never persisted) — the exact replay hazard the ledger guards.
        Assert.True(await ledger.TryClaimAsync(rec.IdempotencyKey));
        queue.SeedNormalized(rec);

        var worker = Worker(Cfg(), queue, ledger);
        await worker.RunCycleAsync(default);

        Assert.Equal(0, worker.RecordingsPickedUp);
        Assert.Equal(1, worker.RecordingsReplayed);
        Assert.Equal(EnrichmentState.Normalized, queue.StateOf(rec)); // untouched — no double-apply
    }

    // ── a second cycle over an already-advanced batch does nothing (drain is convergent) ─
    [Fact]
    public async Task A_second_cycle_after_advance_leases_nothing()
    {
        var queue = new InMemoryNormalizedWorkQueue();
        var ledger = new InMemoryEnrichmentLedger();
        var rec = Rec();
        queue.SeedNormalized(rec);
        var worker = Worker(Cfg(), queue, ledger);

        await worker.RunCycleAsync(default); // advances → enriched
        var pickedAfterFirst = worker.RecordingsPickedUp;
        await worker.RunCycleAsync(default); // the lease now returns nothing (row no longer normalized)

        Assert.Equal(1, pickedAfterFirst);
        Assert.Equal(1, worker.RecordingsPickedUp); // no further pickups
    }

    // ── escalate-only holds: the drain never auto-applies (default engine phase) ─────
    [Fact]
    public async Task Escalate_only_holds_the_drain_opens_no_map_pr_and_writes_no_auto_apply()
    {
        // The engine's DEFAULT posture is the gate the drain rides: escalate-only + map-PR dry-run.
        var engineCfg = EnrichmentConfig.From(new Dictionary<string, string?>());
        Assert.False(engineCfg.GradedAutoApply); // escalate-only by default
        Assert.True(engineCfg.MapPrDryRun);       // map-PR dry-run by default

        // The host's ingest-driven drain advances a recording ONLY to `enriched` — it constructs no
        // apply stage, resolves no CervelloGraphWriter/IMapPrWriter, and so can open no map-PR nor
        // write any auto-applied fact. The worker's dependency set is exactly {queue, ingest, ledger}
        // (see the Worker(...) factory) — the apply seam is structurally unreachable from the drain.
        var queue = new InMemoryNormalizedWorkQueue();
        var ledger = new InMemoryEnrichmentLedger();
        var rec = Rec();
        queue.SeedNormalized(rec);

        var worker = Worker(Cfg(), queue, ledger);
        await worker.RunCycleAsync(default);

        // Advanced to `enriched` and NO further along the spine (never bundle_created/graph_pr_opened).
        Assert.Equal(EnrichmentState.Enriched, queue.StateOf(rec));
    }

    // ── error → failed_retryable, batch continues ───────────────────────────────────
    [Fact]
    public async Task A_per_item_error_maps_to_failed_retryable_and_does_not_abort_the_batch()
    {
        var queue = new InMemoryNormalizedWorkQueue();
        // A ledger that throws on the FIRST claim, succeeds after — the first recording fails, the
        // second drains normally. Proves the failure is isolated to the item.
        var ledger = new FailOnceLedger();
        var bad = Rec("bad", "sha-bad");
        var good = Rec("good", "sha-good");
        queue.SeedNormalized(bad);
        queue.SeedNormalized(good);

        var worker = Worker(Cfg(), queue, ledger);
        await worker.RunCycleAsync(default);

        Assert.Equal(1, worker.RecordingsFailedRetryable);
        // The failing item was advanced to failed_retryable (SCHEMAS §5), not left silently normalized.
        Assert.Equal(EnrichmentState.FailedRetryable, queue.StateOf(bad));
        // The batch continued: the good item still drained.
        Assert.Equal(1, worker.RecordingsPickedUp);
        Assert.Equal(EnrichmentState.Enriched, queue.StateOf(good));
    }

    // ── bounded batch: the lease is capped at BatchSize ─────────────────────────────
    [Fact]
    public async Task The_lease_is_bounded_by_batch_size()
    {
        var queue = new InMemoryNormalizedWorkQueue();
        var ledger = new InMemoryEnrichmentLedger();
        for (var i = 0; i < 10; i++)
            queue.SeedNormalized(Rec($"id{i}", $"sha{i}"));

        var worker = Worker(Cfg(batch: 3), queue, ledger);
        await worker.RunCycleAsync(default);

        Assert.Equal(3, worker.RecordingsPickedUp); // only the batch-size window drained this cycle
    }

    /// <summary>A ledger whose FIRST TryClaim throws (transient failure), then behaves normally.</summary>
    private sealed class FailOnceLedger : IEnrichmentLedger
    {
        private readonly InMemoryEnrichmentLedger _inner = new();
        private bool _thrown;

        public Task<bool> TryClaimAsync(string idempotencyKey, CancellationToken ct = default)
        {
            if (!_thrown)
            {
                _thrown = true;
                throw new InvalidOperationException("transient claim failure (simulated 5xx)");
            }
            return _inner.TryClaimAsync(idempotencyKey, ct);
        }

        public Task<bool> IsClaimedAsync(string idempotencyKey, CancellationToken ct = default) =>
            _inner.IsClaimedAsync(idempotencyKey, ct);
    }
}
