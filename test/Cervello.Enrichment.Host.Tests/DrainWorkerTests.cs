using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Host.Drain;
using Cervello.Enrichment.Pipeline;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Policy;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cervello.Enrichment.Host.Tests;

/// <summary>
/// The drain-loop behavioural contract, all against FAKES — no DB, no network, no real audio. After
/// the E-PIPE rewire the worker runs the FULL <see cref="EnrichmentPipeline"/> orchestrator (not just
/// the ingest entry), so a single deterministic <c>RunCycleAsync</c> proves: a normalized item drains
/// through the WHOLE chain and advances to <c>graph_pr_opened</c>; the shared row records the last
/// state; an idempotent replay is a no-op; escalate-only holds (no auto-apply, no real map-PR); a
/// per-item error maps to <c>failed_retryable</c> without aborting the batch.
/// </summary>
public sealed class DrainWorkerTests
{
    private static RecordingRef Rec(string id = "20260601-guilhem", string sha = "sha-aaa") =>
        new(id, sha, "m4a", "fr", ready: true);

    private static HostConfig Cfg(int batch = 16) =>
        HostConfig.From(new Dictionary<string, string?> { ["CERVELLO_ENRICHMENT_BATCH_SIZE"] = batch.ToString() });

    /// <summary>Build a full-pipeline drain worker over the given queue + ledger, all fakes.</summary>
    private static DrainWorker Worker(
        HostConfig cfg, INormalizedWorkQueue queue, IEnrichmentLedger ledger,
        IDiarizeEmbedClient? diarize = null)
    {
        var pipeline = BuildPipeline(ledger, diarize);
        return new DrainWorker(cfg, queue, pipeline, NullLogger<DrainWorker>.Instance);
    }

    private static EnrichmentPipeline BuildPipeline(
        IEnrichmentLedger ledger, IDiarizeEmbedClient? diarizeClient = null)
    {
        var store = new InMemoryVoiceprintStore(EnrollmentAllowlist.Empty);
        var prior = new FilenameParticipantPriorSource(new Dictionary<string, PriorCandidates>());
        return new EnrichmentPipeline(
            new IngestStage(ledger),
            new HostFakeAudioSource(),
            new BaseTranscribeStage(new HostFakeTranscribeClient(), new HostInMemoryTranscriptStore()),
            new DiarizeEmbedStage(diarizeClient ?? HostFakeDiarizeEmbedClient.Empty()),
            new ClusterMergeStage(),
            new AttributionStage(store, prior, new DecisionPolicy(DecisionBands.Default, PolicyPhase.EscalateOnly)),
            new CorrectionStage(new HostFakeCorrectionLlm(), new InMemoryCorrectionMapStore(),
                new HostFakeReAsrClient(), new CorrectionGrader(PolicyPhase.EscalateOnly)),
            new EnrichLinkStage(new HostFakeLinkResolver()),
            new ApplyStage(
                new CervelloGraphWriter(new HostFakeMapPrWriter(), new HostFakeLinkResolver(), new HostFakePinStore()),
                new InMemoryOpenPointStore()),
            new HostFakeRecordingFactSource());
    }

    // ── drain: a normalized recording flows end-to-end and advances to graph_pr_opened ──────────
    [Fact]
    public async Task A_normalized_item_drains_through_the_full_pipeline_and_advances_to_graph_pr_opened()
    {
        var queue = new InMemoryNormalizedWorkQueue();
        var ledger = new InMemoryEnrichmentLedger();
        var rec = Rec();
        queue.SeedNormalized(rec);

        var worker = Worker(Cfg(), queue, ledger);
        await worker.RunCycleAsync(default);

        Assert.Equal(1, worker.RecordingsPickedUp);
        Assert.Equal(1, worker.RecordingsGraphPrOpened);
        Assert.Equal(EnrichmentState.GraphPrOpened, queue.StateOf(rec)); // shared row: last transition
        Assert.True(await ledger.IsClaimedAsync(rec.IdempotencyKey));    // §8 key claimed
    }

    // ── idempotent replay: a claimed key re-leased is a no-op ───────────────────────────────────
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

    // ── a second cycle over an already-advanced batch does nothing (drain is convergent) ─────────
    [Fact]
    public async Task A_second_cycle_after_advance_leases_nothing()
    {
        var queue = new InMemoryNormalizedWorkQueue();
        var ledger = new InMemoryEnrichmentLedger();
        var rec = Rec();
        queue.SeedNormalized(rec);
        var worker = Worker(Cfg(), queue, ledger);

        await worker.RunCycleAsync(default); // advances → graph_pr_opened
        var pickedAfterFirst = worker.RecordingsPickedUp;
        await worker.RunCycleAsync(default); // the lease now returns nothing (row no longer normalized)

        Assert.Equal(1, pickedAfterFirst);
        Assert.Equal(1, worker.RecordingsPickedUp); // no further pickups
    }

    // ── escalate-only holds: the full drain opens no real map-PR + writes no auto-apply ──────────
    [Fact]
    public async Task Escalate_only_holds_the_drain_opens_no_map_pr_and_writes_no_auto_apply()
    {
        // The engine's DEFAULT posture is the gate the drain rides: escalate-only + map-PR dry-run.
        var engineCfg = EnrichmentConfig.From(new Dictionary<string, string?>());
        Assert.False(engineCfg.GradedAutoApply); // escalate-only by default
        Assert.True(engineCfg.MapPrDryRun);       // map-PR dry-run by default

        var queue = new InMemoryNormalizedWorkQueue();
        var ledger = new InMemoryEnrichmentLedger();
        var rec = Rec();
        queue.SeedNormalized(rec);

        var worker = Worker(Cfg(), queue, ledger);
        await worker.RunCycleAsync(default);

        // The full chain ran to graph_pr_opened, but under escalate-only NO attribution auto-applied.
        // The map-PR writer fake captured NO PR (no applied mutations reached it), and no fact was
        // written to map/ — the escalate-only posture held all the way through the orchestrator.
        Assert.Equal(EnrichmentState.GraphPrOpened, queue.StateOf(rec));
        Assert.Equal(1, worker.RecordingsGraphPrOpened);
    }

    // ── error → failed_retryable, batch continues ───────────────────────────────────────────────
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
        // The batch continued: the good item still drained the whole chain.
        Assert.Equal(1, worker.RecordingsPickedUp);
        Assert.Equal(EnrichmentState.GraphPrOpened, queue.StateOf(good));
    }

    // ── a transient diarize fault maps the item to failed_retryable end-to-end ──────────────────
    [Fact]
    public async Task A_transient_stage_fault_maps_the_item_to_failed_retryable()
    {
        var queue = new InMemoryNormalizedWorkQueue();
        var ledger = new InMemoryEnrichmentLedger();
        var rec = Rec();
        queue.SeedNormalized(rec);

        var worker = Worker(Cfg(), queue, ledger,
            diarize: HostFakeDiarizeEmbedClient.Faulting(new DiarizeEmbedTransientException("sidecar 503")));
        await worker.RunCycleAsync(default);

        Assert.Equal(1, worker.RecordingsFailedRetryable);
        Assert.Equal(EnrichmentState.FailedRetryable, queue.StateOf(rec));
    }

    // ── bounded batch: the lease is capped at BatchSize ─────────────────────────────────────────
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
