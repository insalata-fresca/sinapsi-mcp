using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Host.Drain;
using Cervello.Enrichment.Pipeline.Stages;
using Cervello.Enrichment.Ports;
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
/// <para><b>One cycle.</b> Lease a bounded batch of recordings in <c>normalized</c> from
/// <see cref="INormalizedWorkQueue"/> → for each, run the engine's <see cref="IngestStage"/>
/// (which atomically CLAIMS the §8 idempotency key via <see cref="IEnrichmentLedger"/> — a replay of
/// a seen key is a logged no-op — and advances <c>normalized → enriched</c> under the escalate-only
/// gate) → persist the advanced state back to the shared row. A per-item throw maps the recording to
/// <c>failed_retryable</c> (SCHEMAS §5) and continues the batch; it does not abort the cycle.</para>
///
/// <para><b>Scope boundary (E-HOST).</b> This host owns the drain MECHANISM: poll → claim → run the
/// pipeline ENTRY (ingest) → advance state → idempotent replay → escalate-only → failure mapping →
/// health. Threading ALL eight stages end-to-end (diarize/attribute/correct/enrich/bundle/apply) into
/// a single recording's run requires per-stage inputs derived from real audio + a stage-to-stage
/// data-flow the L1 engine library deliberately did not ship (no uniform stage interface, no
/// orchestrator). That full inter-stage orchestrator is a FOLLOW-UP mission (flagged in the E-HOST
/// return as a STOP/handoff item), NOT E-HOST. The <see cref="IngestStage"/> is the one composable,
/// self-contained entry the library provides, and it is what the host drives here.</para>
/// </summary>
public sealed class DrainWorker : BackgroundService
{
    private readonly HostConfig _cfg;
    private readonly INormalizedWorkQueue _queue;
    private readonly IngestStage _ingest;
    private readonly IEnrichmentLedger _ledger;
    private readonly ILogger<DrainWorker> _log;

    public bool Ready { get; private set; }
    public long RecordingsPickedUp { get; private set; }
    public long RecordingsReplayed { get; private set; }
    public long RecordingsFailedRetryable { get; private set; }

    public DrainWorker(
        HostConfig cfg,
        INormalizedWorkQueue queue,
        IngestStage ingest,
        IEnrichmentLedger ledger,
        ILogger<DrainWorker> log)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _ingest = ingest ?? throw new ArgumentNullException(nameof(ingest));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
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
    /// Drive one recording through the pipeline entry (ingest) and persist the advanced state.
    /// A throw is mapped to <c>failed_retryable</c> (SCHEMAS §5) and swallowed so the batch continues.
    /// </summary>
    public async Task ProcessAsync(RecordingRef recording, CancellationToken ct)
    {
        try
        {
            // The engine's ingest stage is the eligibility + idempotency gate: it only advances a
            // recording that is `normalized` + ready, and it CLAIMS the §8 key atomically. A seen key
            // → Replay (no-op); a fresh key → PickedUp with State advanced to `enriched`.
            var result = await _ingest.IngestAsync(recording, EnrichmentState.Normalized, ct).ConfigureAwait(false);

            if (result.IsReplay)
            {
                RecordingsReplayed++;
                _log.LogInformation("drain {Key}: replay — no-op (idempotency key already claimed)",
                    recording.IdempotencyKey);
                return; // idempotent: leave the shared state row untouched
            }

            if (!result.PickedUp)
            {
                // Not eligible (not normalized / not ready) — leave the row for a later cycle. In the
                // drain path the lease already filtered to `normalized`, so this is defensive.
                _log.LogDebug("drain {Key}: not eligible ({Reason})", recording.IdempotencyKey, result.Reason);
                return;
            }

            // Picked up + advanced normalized → enriched. Persist the advance to the shared row so the
            // Watcher's `normalized` no longer re-leases it. (The escalate-only + dry-run posture is
            // carried by the engine's DecisionPolicy/map-PR config — the host flips no auto-apply.)
            await _queue.AdvanceStateAsync(recording, result.State, reason: null, ct).ConfigureAwait(false);
            RecordingsPickedUp++;
            _log.LogInformation("drain {Key}: picked up → {State}",
                recording.IdempotencyKey, EnrichmentStateMachine.Name(result.State));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown — let the loop unwind cleanly
        }
        catch (Exception e)
        {
            // Transient/opaque failure for THIS recording → failed_retryable (SCHEMAS §5: retried
            // under the same idempotency key on a later cycle). Never crash the whole drain on one item.
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
