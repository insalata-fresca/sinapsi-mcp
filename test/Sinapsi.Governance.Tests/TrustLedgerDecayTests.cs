using Sinapsi.Governance;
using Sinapsi.Governance.Events;
using Xunit;

namespace Sinapsi.Governance.Tests;

/// <summary>
/// The trust-ledger invariants (docs/64 §3): ratchet-up only on proven reliability,
/// decay-to-baseline on any miss, a starvation floor, instant revoke, and the trust-plane
/// hard cap. These are the load-bearing decay tests the D1 DoD calls out.
/// </summary>
public sealed class TrustLedgerDecayTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
    private static TrustLedger NewLedger(IGovernanceEventSink? sink = null) =>
        new(TrustLedgerConfig.Default, clock: () => T0, sink: sink);

    [Fact]
    public void ColdStart_IsBaseline_AtFloor_NotAutoProceed()
    {
        var ledger = NewLedger();
        var e = ledger.Get(ChangeClass.ApplicationCode);

        Assert.Equal(AutoProceedAuthority.Baseline, e.Authority);
        Assert.Equal(TrustLedgerConfig.DefaultFloor, e.Score, 3);
        Assert.False(e.MayAutoProceed);
    }

    [Fact]
    public void RatchetsUp_ToEarned_OnlyAfterSustainedReliability()
    {
        var ledger = NewLedger();

        // Climbs through Baseline → Probationary, staying non-auto until score AND streak clear the bar.
        for (int i = 0; i < 4; i++)
        {
            var mid = ledger.RecordShadowOutcome(ChangeClass.ApplicationCode, ShadowOutcome.Reliable);
            Assert.False(mid.MayAutoProceed);
        }

        var earned = ledger.RecordShadowOutcome(ChangeClass.ApplicationCode, ShadowOutcome.Reliable);
        Assert.Equal(AutoProceedAuthority.Earned, earned.Authority);
        Assert.True(earned.MayAutoProceed);
        Assert.True(earned.ConsecutiveReliable >= TrustLedgerConfig.Default.RatchetConfirmations);
    }

    [Fact]
    public void ConfirmationGate_IsIndependentOfScore()
    {
        // A single huge reliable step reaches full score but NOT enough confirmations → still not auto.
        var cfg = TrustLedgerConfig.Default with { RatchetStep = 1.0 };
        var ledger = new TrustLedger(cfg, clock: () => T0);

        var one = ledger.RecordShadowOutcome(ChangeClass.ApplicationCode, ShadowOutcome.Reliable);
        Assert.Equal(1.0, one.Score, 3);
        Assert.Equal(1, one.ConsecutiveReliable);
        Assert.False(one.MayAutoProceed); // score maxed, but streak 1 < 3 confirmations

        ledger.RecordShadowOutcome(ChangeClass.ApplicationCode, ShadowOutcome.Reliable);
        var three = ledger.RecordShadowOutcome(ChangeClass.ApplicationCode, ShadowOutcome.Reliable);
        Assert.True(three.MayAutoProceed); // now 3 confirmations
    }

    [Fact]
    public void AnyMiss_DecaysTowardBaseline_AndDropsAutoProceed()
    {
        var ledger = NewLedger();
        for (int i = 0; i < 5; i++) ledger.RecordShadowOutcome(ChangeClass.ApplicationCode, ShadowOutcome.Reliable);
        var earned = ledger.Get(ChangeClass.ApplicationCode);
        Assert.True(earned.MayAutoProceed);

        var afterMiss = ledger.RecordShadowOutcome(ChangeClass.ApplicationCode, ShadowOutcome.Miss);

        Assert.False(afterMiss.MayAutoProceed);                 // one miss loses earned authority
        Assert.Equal(0, afterMiss.ConsecutiveReliable);         // streak reset
        Assert.True(afterMiss.Score < earned.Score);            // decayed
        Assert.Equal(earned.Score * TrustLedgerConfig.Default.DecayFactor, afterMiss.Score, 3);
    }

    [Fact]
    public void Decay_NeverGoesBelowFloor_NoStarvation()
    {
        var ledger = NewLedger();
        for (int i = 0; i < 20; i++) ledger.RecordShadowOutcome(ChangeClass.ApplicationCode, ShadowOutcome.Miss);
        var e = ledger.Get(ChangeClass.ApplicationCode);

        Assert.Equal(TrustLedgerConfig.DefaultFloor, e.Score, 3); // clamped at the floor, not zero
        Assert.True(e.Score > 0.0);
        Assert.Equal(AutoProceedAuthority.Baseline, e.Authority);
    }

    [Fact]
    public void Revoke_IsInstant_BypassesFloor_AndSticksThroughReliableOutcomes()
    {
        var ledger = NewLedger();
        for (int i = 0; i < 5; i++) ledger.RecordShadowOutcome(ChangeClass.ApplicationCode, ShadowOutcome.Reliable);
        Assert.True(ledger.MayAutoProceed(ChangeClass.ApplicationCode));

        var revoked = ledger.Revoke(ChangeClass.ApplicationCode, "operator kill switch");
        Assert.Equal(AutoProceedAuthority.Revoked, revoked.Authority);
        Assert.Equal(0.0, revoked.Score, 3);                    // below the floor — deliberate, not decay
        Assert.False(revoked.MayAutoProceed);

        // A revoked class must not be silently un-revoked by a good shadow run.
        var afterReliable = ledger.RecordShadowOutcome(ChangeClass.ApplicationCode, ShadowOutcome.Reliable);
        Assert.True(afterReliable.Revoked);
        Assert.False(afterReliable.MayAutoProceed);

        var reinstated = ledger.Reinstate(ChangeClass.ApplicationCode);
        Assert.False(reinstated.Revoked);
        Assert.Equal(AutoProceedAuthority.Baseline, reinstated.Authority); // must re-earn from scratch
    }

    [Fact]
    public void Revoke_RequiresReason()
    {
        var ledger = NewLedger();
        Assert.Throws<ArgumentException>(() => ledger.Revoke(ChangeClass.InfraConfig, "  "));
    }

    [Fact]
    public void TrustPlane_NeverEarnsAutoProceed_NoMatterHowReliable()
    {
        var ledger = NewLedger();
        for (int i = 0; i < 50; i++) ledger.RecordShadowOutcome(ChangeClass.TrustPlane, ShadowOutcome.Reliable);
        var e = ledger.Get(ChangeClass.TrustPlane);

        Assert.False(e.MayAutoProceed);                                     // the §2 invariant
        Assert.NotEqual(AutoProceedAuthority.Earned, e.Authority);
        Assert.True(e.Score <= TrustLedgerConfig.Default.CeilingFor(ChangeClass.TrustPlane) + 1e-9); // capped
        Assert.True(TrustLedgerConfig.Default.IsAutoProceedForbidden(ChangeClass.TrustPlane));
    }

    [Fact]
    public void EveryMutation_EmitsAGovernanceFact()
    {
        var sink = new RecordingGovernanceEventSink();
        var ledger = NewLedger(sink);

        ledger.RecordShadowOutcome(ChangeClass.DocsOnly, ShadowOutcome.Reliable);
        ledger.Revoke(ChangeClass.DocsOnly, "test");

        Assert.Equal(2, sink.Events.Count);
        Assert.All(sink.Events, ev => Assert.Equal("trust", ev.Kind));
        // Subjects are governance FACTS (the recording sink asserts EnsureFact on capture).
        Assert.All(sink.Events, ev => Assert.StartsWith("homelab.governance.trust.", ev.Subject));
    }
}
