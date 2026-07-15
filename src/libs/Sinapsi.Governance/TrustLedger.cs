using Sinapsi.Governance.Events;

namespace Sinapsi.Governance;

/// <summary>
/// Graduated, revocable, per-change-class trust — the machine that keeps the delivery
/// evaluator honest over time (home-server <c>docs/64 §3</c>, <c>docs/66</c>, Mission D1).
///
/// <para>Trust is <b>data the evaluator/pipeline reads</b>, not a second gate: call
/// <see cref="AuthorityFor"/> / <see cref="MayAutoProceed"/> to learn whether a class may
/// auto-proceed. The ledger itself only <i>observes shadow outcomes and revocations</i> and
/// recomputes authority — it never merges or deploys anything.</para>
///
/// <para>Every mutation emits a governance FACT (via the injected
/// <see cref="IGovernanceEventSink"/>) so the ledger is auditable and a revocation is
/// broadcast the instant it happens. The core math is pure + deterministic (clock injected),
/// so the ratchet/decay/floor/revoke invariants are unit-testable without a bus.</para>
///
/// <para>Not thread-safe; drive it from a single worker (the governance host), as with the
/// other single-consumer event-plane workers.</para>
/// </summary>
public sealed class TrustLedger
{
    private readonly TrustLedgerConfig _config;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IGovernanceEventSink _sink;
    private readonly Dictionary<ChangeClass, TrustLedgerEntry> _entries = new();

    public TrustLedger(
        TrustLedgerConfig? config = null,
        Func<DateTimeOffset>? clock = null,
        IGovernanceEventSink? sink = null)
    {
        _config = config ?? TrustLedgerConfig.Default;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _sink = sink ?? NullGovernanceEventSink.Instance;
    }

    /// <summary>The current entry for a class (a cold-start baseline if none recorded yet).</summary>
    public TrustLedgerEntry Get(ChangeClass changeClass) =>
        _entries.TryGetValue(changeClass, out var e)
            ? e
            : TrustLedgerEntry.Baseline(changeClass, _config.Floor, _clock());

    /// <summary>The authority datum the pipeline reads for a class.</summary>
    public AutoProceedAuthority AuthorityFor(ChangeClass changeClass) => Get(changeClass).Authority;

    /// <summary>Convenience: may this class auto-proceed on the green path right now?</summary>
    public bool MayAutoProceed(ChangeClass changeClass) => Get(changeClass).MayAutoProceed;

    /// <summary>
    /// Feed one shadow decision's outcome back into the class's trust. Reliable ratchets
    /// the score up (bounded by the class ceiling) and extends the confirmation streak;
    /// a miss decays it toward the floor and zeroes the streak. Returns the new entry and
    /// emits a <c>trust</c> fact.
    /// </summary>
    public TrustLedgerEntry RecordShadowOutcome(ChangeClass changeClass, ShadowOutcome outcome)
    {
        var prev = Get(changeClass);

        // A revoked class stays revoked until an explicit Reinstate — a shadow outcome
        // (even a reliable one) must not silently un-revoke a killed class.
        if (prev.Revoked)
        {
            _sink.Emit(GovernanceEvent.Trust(prev, outcome, note: "outcome ignored: class is revoked"));
            return prev;
        }

        double score = outcome switch
        {
            ShadowOutcome.Reliable => Math.Min(_config.CeilingFor(changeClass), prev.Score + _config.RatchetStep),
            ShadowOutcome.Miss => Math.Max(_config.Floor, prev.Score * _config.DecayFactor),
            _ => prev.Score,
        };
        int streak = outcome == ShadowOutcome.Reliable ? prev.ConsecutiveReliable + 1 : 0;

        var entry = prev with
        {
            Score = score,
            ConsecutiveReliable = streak,
            Authority = ComputeAuthority(changeClass, score, streak, revoked: false),
            LastOutcome = outcome,
            UpdatedAt = _clock(),
        };
        _entries[changeClass] = entry;
        _sink.Emit(GovernanceEvent.Trust(entry, outcome, note: null));
        return entry;
    }

    /// <summary>
    /// Instantly revoke a class's trust — the kill switch. Score is driven to 0 (below the
    /// starvation floor: revocation is deliberate, not decay) and authority to
    /// <see cref="AutoProceedAuthority.Revoked"/>, taking effect the next time the pipeline
    /// reads it. Idempotent.
    /// </summary>
    public TrustLedgerEntry Revoke(ChangeClass changeClass, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("a revocation must carry a reason", nameof(reason));

        var entry = Get(changeClass) with
        {
            Score = 0.0,
            ConsecutiveReliable = 0,
            Authority = AutoProceedAuthority.Revoked,
            Revoked = true,
            RevokedReason = reason,
            UpdatedAt = _clock(),
        };
        _entries[changeClass] = entry;
        _sink.Emit(GovernanceEvent.Trust(entry, outcome: null, note: $"REVOKED: {reason}"));
        return entry;
    }

    /// <summary>
    /// Lift a revocation, returning the class to the conservative baseline (floor,
    /// escalate-by-default) — it must re-earn authority from scratch. No-op on a
    /// non-revoked class.
    /// </summary>
    public TrustLedgerEntry Reinstate(ChangeClass changeClass)
    {
        var prev = Get(changeClass);
        if (!prev.Revoked) return prev;

        var entry = TrustLedgerEntry.Baseline(changeClass, _config.Floor, _clock());
        _entries[changeClass] = entry;
        _sink.Emit(GovernanceEvent.Trust(entry, outcome: null, note: "reinstated to baseline"));
        return entry;
    }

    /// <summary>A snapshot of the whole ledger (baseline entries materialised for any unseen class).</summary>
    public IReadOnlyList<TrustLedgerEntry> Snapshot() =>
        ChangeClassOrdering.All.Select(Get).ToList();

    private AutoProceedAuthority ComputeAuthority(ChangeClass changeClass, double score, int streak, bool revoked)
    {
        if (revoked) return AutoProceedAuthority.Revoked;

        bool earnedBar = score >= _config.EarnedThreshold
                         && streak >= _config.RatchetConfirmations
                         && !_config.IsAutoProceedForbidden(changeClass);
        if (earnedBar) return AutoProceedAuthority.Earned;

        return score >= _config.ProbationThreshold
            ? AutoProceedAuthority.Probationary
            : AutoProceedAuthority.Baseline;
    }
}
