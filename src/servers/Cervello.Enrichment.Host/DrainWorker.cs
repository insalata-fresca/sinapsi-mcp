using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Host.Drain;
using Cervello.Enrichment.Pipeline;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cervello.Enrichment.Host;

/// <summary>
/// The enrichment DRAIN loop (the deploy-slice worker). Mirrors the Watcher's
/// <c>WatchWorker</c> shape — a <see cref="BackgroundService"/> with a bounded poll cycle, a
/// readiness flag for the health endpoint, graceful cancellation, and a per-cycle try/catch that
/// backs off to the next interval. It references NEITHER Sinapsi.Nats NOR any NATS client
/// (invariant 3 / D8): the "work available" signal is the shared <c>normalized</c> state row, polled,
/// never a bus message.
///
/// <para><b>One cycle (MISSION E-PIPE rewire).</b> Lease a bounded batch of recordings in
/// <c>normalized</c> from <see cref="INormalizedWorkQueue"/> → for each, run the FULL
/// <see cref="EnrichmentPipeline"/> orchestrator (ingest → transcribe → diarize+embed → cluster-merge
/// → attribute → correct → enrich+link → apply), which CLAIMS the §8 idempotency key (a replay of a
/// seen key is a logged no-op) and advances the SCHEMAS §5 state at EACH transition all the way to
/// <c>graph_pr_opened</c> (or escalate-only equivalent) → persist each advanced state back to the
/// shared row. A per-item throw / stage failure maps the recording to <c>failed_retryable</c> (or
/// <c>failed_terminal</c> for a contract violation) and continues the batch; it does not abort the
/// cycle.</para>
///
/// <para><b>Scope (E-PIPE).</b> Previously (E-HOST #62) this host drove only the pipeline ENTRY
/// (<c>IngestStage</c>, advancing <c>normalized → enriched</c>) because no orchestrator threaded the
/// stages. E-PIPE built <see cref="EnrichmentPipeline"/> — the orchestrator threading all stages
/// end-to-end — and this worker now runs it: a <c>normalized</c> recording flows through the whole
/// chain to <c>graph_pr_opened</c>/escalated. The escalate-only + map-PR dry-run posture is carried
/// by the engine's <c>DecisionPolicy</c> / map-PR adapter config (the host flips no auto-apply).</para>
/// </summary>
public sealed class DrainWorker : BackgroundService
{
    private readonly HostConfig _cfg;
    private readonly INormalizedWorkQueue _queue;
    private readonly EnrichmentPipeline _pipeline;
    private readonly ILogger<DrainWorker> _log;

    public bool Ready { get; private set; }
    public long RecordingsPickedUp { get; private set; }
    public long RecordingsReplayed { get; private set; }
    public long RecordingsFailedRetryable { get; private set; }
    public long RecordingsFailedTerminal { get; private set; }
    public long RecordingsGraphPrOpened { get; private set; }

    public DrainWorker(
        HostConfig cfg,
        INormalizedWorkQueue queue,
        EnrichmentPipeline pipeline,
        ILogger<DrainWorker> log)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("cervello-enrichment-host starting (no NATS; drain poll {Interval}s, batch {Batch})",
            _cfg.PollIntervalSeconds, _cfg.BatchSize);
        Ready = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                // A whole-cycle failure (e.g. the lease query itself threw) — log and back off to the
                // next interval. Per-ITEM failures are handled inside RunCycleAsync (→ failed_retryable).
                _log.LogError(e, "drain cycle failed; retrying next poll");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_cfg.PollIntervalSeconds), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// One drain cycle: lease a bounded batch of <c>normalized</c> recordings and process each.
    /// Public so tests drive a single deterministic cycle without the timer loop.
    /// </summary>
    public async Task RunCycleAsync(CancellationToken ct)
    {
        var batch = await _queue.LeaseNormalizedAsync(_cfg.BatchSize, ct).ConfigureAwait(false);
        if (batch.Count == 0)
            return;

        _log.LogInformation("drain cycle: {Count} recording(s) in normalized", batch.Count);
        foreach (var recording in batch)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessAsync(recording, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drive one recording through the FULL <see cref="EnrichmentPipeline"/> and persist each state
    /// transition to the shared row. A replay is a no-op; a completed run reaches
    /// <c>graph_pr_opened</c>; a stage failure maps to <c>failed_retryable</c> / <c>failed_terminal</c>
    /// (SCHEMAS §5). A defensive throw is also mapped to <c>failed_retryable</c> so the batch continues.
    /// </summary>
    public async Task ProcessAsync(RecordingRef recording, CancellationToken ct)
    {
        try
        {
            // Run the whole chain. The pipeline's ingest step is the eligibility + idempotency gate
            // (a seen key → Replay no-op; a fresh key → runs every stage), and it advances the §5
            // state at each transition. The escalate-only + map-PR dry-run posture is carried by the
            // engine's DecisionPolicy / map-PR config — the host flips no auto-apply.
            var outcome = await _pipeline.RunAsync(recording, EnrichmentState.Normalized, ct).ConfigureAwait(false);

            if (outcome.IsReplay)
            {
                // A replay = the §8 idempotency key was already CLAIMED in a prior run that ran the
                // recording all the way to its terminal-success state (transcript in git, open-points
                // recorded). The enrichment itself is NOT re-run (the ledger claim guards that). But we
                // MUST NOT leave the shared row at `normalized`: the drain lease selects the EARLIEST
                // `normalized` row (batch-ordered by ready_at), so a claimed-but-still-`normalized` row
                // is re-selected every cycle FOREVER and no other recording ever drains — a hard queue
                // deadlock (incident 2026-07-09: a self-heal re-backfill reset 3 already-enriched
                // recordings to `normalized`, wedging the whole 76-row backlog on the earliest one).
                //
                // The correct, idempotent resolution is to ADVANCE the replay row OUT of `normalized`
                // to `graph_pr_opened` — its already-completed terminal-success state (a legal forward
                // spine jump, SCHEMAS §5: normalized → graph_pr_opened). This re-runs NO enrichment and
                // cannot loop: next cycle the lease no longer sees the row and moves to the next
                // `normalized` recording. Persisting the terminal state converges the shared §5 row to
                // the reality the ledger already reflects.
                RecordingsReplayed++;
                await _queue.AdvanceStateAsync(
                    recording, EnrichmentState.GraphPrOpened,
                    reason: "replay — idempotency key already claimed (already fully enriched in a prior run)",
                    ct).ConfigureAwait(false);
                _log.LogInformation(
                    "drain {Key}: replay — advanced {From} → {To} (already enriched; no re-run, breaks the drain deadlock)",
                    recording.IdempotencyKey,
                    EnrichmentStateMachine.Name(EnrichmentState.Normalized),
                    EnrichmentStateMachine.Name(EnrichmentState.GraphPrOpened));
                return;
            }

            if (outcome.Status == Pipeline.PipelineStatus.NotEligible)
            {
                // Not eligible (not normalized / not ready). In the drain path the lease already filtered
                // to `normalized` and always sets ready=true, so this is UNREACHABLE today — but if it
                // ever fires we must NOT leave the row at `normalized`, or the lease re-selects the same
                // earliest row every cycle and re-deadlocks the whole backlog (the exact hazard the
                // replay branch above fixes). Advance it to `failed_retryable` (a legal §5 exit from any
                // live spine state) so the row leaves `normalized`; a genuinely-eligible-later recording
                // is retried under the same idempotency key.
                RecordingsFailedRetryable++;
                _log.LogWarning("drain {Key}: not eligible ({Reason}) → failed_retryable (leaves normalized; retried next cycle)",
                    recording.IdempotencyKey, outcome.Reason);
                await _queue.AdvanceStateAsync(
                    recording, EnrichmentState.FailedRetryable,
                    reason: outcome.Reason ?? "not eligible", ct).ConfigureAwait(false);
                return;
            }

            // Persist EVERY transition the run took to the shared row (normalized → enriched →
            // attention_scored → bundle_created → graph_pr_opened, or a failure sink), so the Watcher's
            // `normalized` no longer re-leases it and the shared §5 row reflects reality.
            foreach (var state in outcome.StateTransitions)
                await _queue.AdvanceStateAsync(recording, state, reason: outcome.Reason, ct).ConfigureAwait(false);

            switch (outcome.Status)
            {
                case Pipeline.PipelineStatus.Completed:
                    RecordingsPickedUp++;
                    if (outcome.State == EnrichmentState.GraphPrOpened) RecordingsGraphPrOpened++;
                    _log.LogInformation("drain {Key}: {From} → {State} (bundle {Bundle}, pr={Pr})",
                        recording.IdempotencyKey, EnrichmentStateMachine.Name(EnrichmentState.Normalized),
                        EnrichmentStateMachine.Name(outcome.State),
                        outcome.Bundle?.BundleId ?? "(none)",
                        outcome.Apply?.Pr?.Branch ?? "(none — escalate-only)");
                    break;

                case Pipeline.PipelineStatus.Failed when outcome.State == EnrichmentState.FailedTerminal:
                    RecordingsFailedTerminal++;
                    _log.LogError("drain {Key}: failed → failed_terminal ({Reason})",
                        recording.IdempotencyKey, outcome.Reason);
                    break;

                case Pipeline.PipelineStatus.Failed:
                    RecordingsFailedRetryable++;
                    _log.LogWarning("drain {Key}: failed → failed_retryable ({Reason}; retried next cycle)",
                        recording.IdempotencyKey, outcome.Reason);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown — let the loop unwind cleanly
        }
        catch (Exception e)
        {
            // A throw OUTSIDE the pipeline's own handling (e.g. the state persist itself threw) →
            // failed_retryable. Never crash the whole drain on one item.
            RecordingsFailedRetryable++;
            _log.LogError(e, "drain {Key}: failed → failed_retryable (retried next cycle)", recording.IdempotencyKey);
            try
            {
                await _queue.AdvanceStateAsync(
                    recording, EnrichmentState.FailedRetryable, reason: e.Message, ct).ConfigureAwait(false);
            }
            catch (Exception persistError)
            {
                _log.LogError(persistError, "drain {Key}: also failed to persist failed_retryable state",
                    recording.IdempotencyKey);
            }
        }
    }
}
