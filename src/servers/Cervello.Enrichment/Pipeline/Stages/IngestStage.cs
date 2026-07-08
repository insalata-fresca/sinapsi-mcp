using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline.Stages;

/// <summary>
/// First enrichment stage (spec <c>enrichment-linking</c> → "Ingest a normalized recording
/// idempotently"). Triggers on a recording that has reached <c>normalized</c> with the
/// WATCH-side ready marker set, keyed by <c>rec:&lt;id&gt;:&lt;sha&gt;</c>. A replay of a seen
/// key is a logged no-op. The stage only ever advances the recording forward (SCHEMAS §5);
/// it never invents a state.
/// </summary>
public sealed class IngestStage(IEnrichmentLedger ledger, ILogger<IngestStage>? logger = null)
{
    private readonly IEnrichmentLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    private readonly ILogger _log = logger ?? NullLogger<IngestStage>.Instance;

    /// <summary>
    /// Attempt to pick up a recording for enrichment. Returns a result describing whether it was
    /// picked up (first time) or a no-op (replay / not ready / not in the normalized state).
    /// </summary>
    public async Task<IngestResult> IngestAsync(
        RecordingRef recording,
        EnrichmentState currentState,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recording);

        // Only a recording at `normalized` with the ready marker is eligible.
        if (currentState != EnrichmentState.Normalized)
        {
            _log.LogDebug("ingest skip {Key}: state {State} is not normalized",
                recording.IdempotencyKey, EnrichmentStateMachine.Name(currentState));
            return IngestResult.NotEligible(currentState, "not in normalized state");
        }

        if (!recording.Ready)
        {
            _log.LogDebug("ingest skip {Key}: ready marker not set", recording.IdempotencyKey);
            return IngestResult.NotEligible(currentState, "ready marker not set");
        }

        // Atomic idempotency claim — a replay of a seen key is a logged no-op.
        var claimed = await _ledger.TryClaimAsync(recording.IdempotencyKey, ct).ConfigureAwait(false);
        if (!claimed)
        {
            _log.LogInformation("ingest no-op {Key}: idempotency key already claimed (replay)",
                recording.IdempotencyKey);
            return IngestResult.Replay(currentState);
        }

        // Advance normalized → enriched (validated forward-only against SCHEMAS §5).
        var next = EnrichmentState.Enriched;
        EnrichmentStateMachine.EnsureLegal(currentState, next);
        _log.LogInformation("ingest picked up {Key}: {From} → {To}",
            recording.IdempotencyKey,
            EnrichmentStateMachine.Name(currentState),
            EnrichmentStateMachine.Name(next));
        return IngestResult.Picked(next);
    }
}

/// <summary>Outcome of <see cref="IngestStage.IngestAsync"/>.</summary>
public sealed record IngestResult
{
    private IngestResult(bool pickedUp, bool replay, EnrichmentState state, string? reason)
    {
        PickedUp = pickedUp;
        IsReplay = replay;
        State = state;
        Reason = reason;
    }

    /// <summary>True iff this call claimed the key and advanced the recording.</summary>
    public bool PickedUp { get; }

    /// <summary>True iff a seen key was replayed (no-op).</summary>
    public bool IsReplay { get; }

    /// <summary>The resulting pipeline state (advanced on pickup; unchanged otherwise).</summary>
    public EnrichmentState State { get; }

    public string? Reason { get; }

    internal static IngestResult Picked(EnrichmentState next) => new(true, false, next, null);
    internal static IngestResult Replay(EnrichmentState state) => new(false, true, state, "replay");
    internal static IngestResult NotEligible(EnrichmentState state, string reason) =>
        new(false, false, state, reason);
}
