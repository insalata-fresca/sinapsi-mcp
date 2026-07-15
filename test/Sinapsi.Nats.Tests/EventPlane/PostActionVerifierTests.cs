using Sinapsi.Nats.EventPlane;
using Xunit;

namespace Sinapsi.Nats.Tests.EventPlane;

/// <summary>C3 item 1 — POST-ACTION VERIFICATION. Proves acknowledged ≠ effective and that the
/// mandatory bake window is enforced (docs/64 §3).</summary>
public sealed class PostActionVerifierTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly BakeWindow Bake = BakeWindow.Require(TimeSpan.FromMinutes(5), minSamples: 3);

    [Fact]
    public void BakeWindow_RejectsZeroDuration()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BakeWindow.Require(TimeSpan.Zero));

    [Fact]
    public void BakeWindow_RejectsSingleSample()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BakeWindow.Require(TimeSpan.FromMinutes(5), minSamples: 1));

    [Fact]
    public void AckWithNoReRead_IsNeverSuccess()
    {
        // The core rule: an acknowledgement, with the changed surface never re-read, is NOT effective.
        var outcome = PostActionVerifier.Evaluate(acknowledged: true, Bake, Array.Empty<EffectSample>());
        Assert.Equal(EffectStatus.AcknowledgedOnly, outcome.Status);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void NeitherAckNorObservation_IsUnverified()
    {
        var outcome = PostActionVerifier.Evaluate(acknowledged: false, Bake, Array.Empty<EffectSample>());
        Assert.Equal(EffectStatus.Unverified, outcome.Status);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void IntendedStateHeldAcrossFullWindow_IsEffective()
    {
        var samples = new[]
        {
            new EffectSample(T0, true),
            new EffectSample(T0.AddMinutes(3), true),
            new EffectSample(T0.AddMinutes(6), true),
        };
        var outcome = PostActionVerifier.Evaluate(acknowledged: true, Bake, samples);
        Assert.Equal(EffectStatus.Effective, outcome.Status);
        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public void MatchesButShorterThanBake_IsStillVerifying_NotSuccess()
    {
        // Two matching reads only 1 minute apart: intended, but the bake (5m/3) is not satisfied.
        var samples = new[]
        {
            new EffectSample(T0, true),
            new EffectSample(T0.AddMinutes(1), true),
        };
        var outcome = PostActionVerifier.Evaluate(acknowledged: true, Bake, samples);
        Assert.Equal(EffectStatus.Verifying, outcome.Status);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void EnoughSpanButTooFewSamples_IsVerifying()
    {
        // 6 minutes apart (span ok) but only 2 samples (< minSamples 3): not baked.
        var samples = new[]
        {
            new EffectSample(T0, true),
            new EffectSample(T0.AddMinutes(6), true),
        };
        var outcome = PostActionVerifier.Evaluate(acknowledged: true, Bake, samples);
        Assert.Equal(EffectStatus.Verifying, outcome.Status);
    }

    [Fact]
    public void EffectThatLeavesIntendedState_IsRegressed_NotSuccess()
    {
        var samples = new[]
        {
            new EffectSample(T0, true),
            new EffectSample(T0.AddMinutes(3), true),
            new EffectSample(T0.AddMinutes(6), false, "surface reverted"),
            new EffectSample(T0.AddMinutes(9), true),
        };
        var outcome = PostActionVerifier.Evaluate(acknowledged: true, Bake, samples);
        Assert.Equal(EffectStatus.Regressed, outcome.Status);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void NeverObservedInIntendedState_IsUnverified()
    {
        var samples = new[]
        {
            new EffectSample(T0, false),
            new EffectSample(T0.AddMinutes(3), false),
        };
        var outcome = PostActionVerifier.Evaluate(acknowledged: true, Bake, samples);
        Assert.Equal(EffectStatus.Unverified, outcome.Status);
    }

    [Fact]
    public void SamplesAreSortedInternally_OrderDoesNotMatter()
    {
        var samples = new[]
        {
            new EffectSample(T0.AddMinutes(6), true),
            new EffectSample(T0, true),
            new EffectSample(T0.AddMinutes(3), true),
        };
        var outcome = PostActionVerifier.Evaluate(acknowledged: true, Bake, samples);
        Assert.Equal(EffectStatus.Effective, outcome.Status);
    }
}
