namespace Sinapsi.Nats.EventPlane;

/// <summary>
/// The verified effect status of an act (home-server <c>docs/64 §3</c> "Verify + rollback"):
/// <b>acknowledged ≠ effective</b>. An API/dispatcher ack proves only that the request was
/// received, never that the CHANGED surface actually took the intended state.
/// </summary>
public enum EffectStatus
{
    /// <summary>The action was acknowledged but the changed surface has NOT been re-read — an ack
    /// alone is NEVER success. This is the trap the canon names: trusting the API return value.</summary>
    AcknowledgedOnly,

    /// <summary>The changed surface has been observed in the intended state, but not yet
    /// continuously across the mandatory bake window — success cannot be declared yet.</summary>
    Verifying,

    /// <summary>The changed surface held the intended state continuously across the full bake
    /// window with enough samples. This — and only this — is "succeeded".</summary>
    Effective,

    /// <summary>The changed surface reached the intended state and then LEFT it during the bake
    /// window (an effect that did not stick). Never success; a candidate for rollback/escalation.</summary>
    Regressed,

    /// <summary>The changed surface was re-read but never observed in the intended state (or was
    /// never acknowledged). Never success.</summary>
    Unverified,
}

/// <summary>One re-read of the CHANGED surface — the event-based SLI sample the canon requires
/// ("event-based SLI on the changed code path"). A sample is an OBSERVATION of the surface, not a
/// report from the actor: <see cref="MatchesIntended"/> is set by re-reading real state.</summary>
/// <param name="ObservedAt">When the surface was re-read.</param>
/// <param name="MatchesIntended">True iff the re-read surface is in the intended post-action state.</param>
/// <param name="Detail">Optional human-readable note (what was read).</param>
public sealed record EffectSample(DateTimeOffset ObservedAt, bool MatchesIntended, string? Detail = null);

/// <summary>The mandatory holding/bake window an effect must survive before it is declared
/// "succeeded" (home-server <c>docs/64 §3</c>). Both a minimum <see cref="Duration"/> AND a minimum
/// number of confirming samples are required — a single lucky read is not a bake.</summary>
public sealed record BakeWindow
{
    private BakeWindow(TimeSpan duration, int minSamples)
    {
        Duration = duration;
        MinSamples = minSamples;
    }

    /// <summary>How long the intended state must hold continuously.</summary>
    public TimeSpan Duration { get; }

    /// <summary>The minimum number of confirming re-reads spanning the window.</summary>
    public int MinSamples { get; }

    /// <summary>Build a bake window. A zero/negative duration or fewer than two samples is rejected:
    /// "no bake" would collapse verification back into trusting the ack, which is the whole hazard.</summary>
    /// <exception cref="ArgumentOutOfRangeException">duration ≤ 0 or minSamples &lt; 2.</exception>
    public static BakeWindow Require(TimeSpan duration, int minSamples = 2)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration),
                "a bake window must be strictly positive — a zero window is just trusting the ack (docs/64 §3).");
        if (minSamples < 2)
            throw new ArgumentOutOfRangeException(nameof(minSamples),
                "a bake requires at least two confirming re-reads spanning the window — one read is not a bake.");
        return new BakeWindow(duration, minSamples);
    }
}

/// <summary>The verified outcome of an act. <see cref="Succeeded"/> is true ONLY for
/// <see cref="EffectStatus.Effective"/> — the deliberate encoding of acknowledged ≠ effective.</summary>
/// <param name="Status">The verified effect status.</param>
/// <param name="Reason">Why (audit).</param>
public sealed record VerificationOutcome(EffectStatus Status, string Reason)
{
    /// <summary>Only an EFFECTIVE, baked observation counts as success. An ack does not; a
    /// mid-bake match does not; a regression does not.</summary>
    public bool Succeeded => Status == EffectStatus.Effective;
}

/// <summary>Re-reads the CHANGED surface for a given end-to-end change-id and returns what it
/// actually observes — the event-based SLI probe on the changed code path (home-server
/// <c>docs/64 §3</c>). The act-path executor implements this to close "acknowledged ≠ effective":
/// after dispatching a merge/deploy it re-reads the merged ref / the deployed revision / the live
/// health signal, NOT the dispatcher's return value. Deliberately unimplemented here (out of scope:
/// wiring a live surface) — this is the seam.</summary>
public interface IEffectProbe
{
    /// <summary>Re-read the changed surface and report whether it is in the intended post-state.</summary>
    ValueTask<EffectSample> ReadAsync(string changeId, CancellationToken ct = default);
}

/// <summary>
/// Turns a set of re-read <see cref="EffectSample"/>s into a <see cref="VerificationOutcome"/>,
/// enforcing the canon's two rules: <b>acknowledged ≠ effective</b> (an ack with no confirming
/// re-read is <see cref="EffectStatus.AcknowledgedOnly"/>, never success) and a <b>mandatory bake
/// window</b> (the intended state must hold continuously for <see cref="BakeWindow.Duration"/> with
/// ≥ <see cref="BakeWindow.MinSamples"/> confirming samples before <see cref="EffectStatus.Effective"/>).
///
/// <para><see cref="Evaluate"/> is PURE over injected samples (no clock, no sleep), so every branch —
/// ack-only, mid-bake, effective, regressed, never-effective — is deterministically unit-testable.
/// The act-path drives it by collecting samples from an <see cref="IEffectProbe"/> over real time.</para>
/// </summary>
public static class PostActionVerifier
{
    /// <summary>Evaluate an act's verified outcome from its ack and the surface re-reads.</summary>
    /// <param name="acknowledged">Whether the dispatcher/API acknowledged the request. NOTE: this
    /// alone can never yield success — it is here only to distinguish "acked but unverified" from
    /// "never even acked".</param>
    /// <param name="window">The mandatory bake window.</param>
    /// <param name="samples">The re-reads of the changed surface, in any order (sorted internally).</param>
    public static VerificationOutcome Evaluate(bool acknowledged, BakeWindow window, IReadOnlyList<EffectSample> samples)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
            return acknowledged
                ? new VerificationOutcome(EffectStatus.AcknowledgedOnly,
                    "acknowledged but the changed surface was never re-read — an ack is not an effect (docs/64 §3)")
                : new VerificationOutcome(EffectStatus.Unverified, "no acknowledgement and no observation of the changed surface");

        var ordered = samples.OrderBy(s => s.ObservedAt).ToList();

        // A regression: the surface matched at some point and then stopped matching.
        var firstMatchIdx = ordered.FindIndex(s => s.MatchesIntended);
        if (firstMatchIdx < 0)
            return new VerificationOutcome(EffectStatus.Unverified,
                "the changed surface was re-read but never observed in the intended state");

        for (var i = firstMatchIdx + 1; i < ordered.Count; i++)
            if (!ordered[i].MatchesIntended)
                return new VerificationOutcome(EffectStatus.Regressed,
                    $"the effect did not stick: observed intended at {ordered[firstMatchIdx].ObservedAt:O} then regressed at {ordered[i].ObservedAt:O}");

        // From firstMatchIdx onward every sample matches. Is the run long/dense enough to be baked?
        var matching = ordered.Skip(firstMatchIdx).ToList();
        var span = matching[^1].ObservedAt - matching[0].ObservedAt;
        if (matching.Count >= window.MinSamples && span >= window.Duration)
            return new VerificationOutcome(EffectStatus.Effective,
                $"intended state held continuously for {span} across {matching.Count} re-reads (bake {window.Duration}/{window.MinSamples} satisfied)");

        return new VerificationOutcome(EffectStatus.Verifying,
            $"intended state observed but bake not yet satisfied ({matching.Count}/{window.MinSamples} samples, {span}/{window.Duration})");
    }
}
