using Sinapsi.Governance;
using Sinapsi.Governance.Inspection;
using Xunit;

namespace Sinapsi.Governance.Tests;

public sealed class RetrospectiveInspectionSamplerTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);

    private static List<AutoProceedDecision> Decisions(int autoProceeded, int escalated)
    {
        var list = new List<AutoProceedDecision>();
        for (int i = 0; i < autoProceeded; i++)
            list.Add(new AutoProceedDecision($"auto-{i:D3}", ChangeClass.ApplicationCode, "allow", true, Day0.AddMinutes(i)));
        for (int i = 0; i < escalated; i++)
            list.Add(new AutoProceedDecision($"esc-{i:D3}", ChangeClass.TrustPlane, "requiresApproval", false, Day0.AddMinutes(i)));
        return list;
    }

    [Fact]
    public void PeriodicSample_OnlyDrawsAutoProceedDecisions()
    {
        var sampler = new RetrospectiveInspectionSampler(clock: () => Day0);
        var sample = sampler.SamplePeriodic(Decisions(autoProceeded: 100, escalated: 100), seed: 1);

        Assert.Equal(InspectionReason.PeriodicRetrospective, sample.Reason);
        Assert.Equal(100, sample.PopulationSize);              // escalations excluded from population
        Assert.All(sample.Decisions, d => Assert.True(d.AutoProceeded));
        Assert.Equal(5, sample.Decisions.Count);              // 5% of 100
    }

    [Fact]
    public void PeriodicSample_IsDeterministicForASeed_ButVariesAcrossSeeds()
    {
        var sampler = new RetrospectiveInspectionSampler(clock: () => Day0);
        var pool = Decisions(autoProceeded: 100, escalated: 0);

        var a1 = sampler.SamplePeriodic(pool, seed: 42).Decisions.Select(d => d.CorrelationId).ToList();
        var a2 = sampler.SamplePeriodic(pool, seed: 42).Decisions.Select(d => d.CorrelationId).ToList();
        var b = sampler.SamplePeriodic(pool, seed: 7).Decisions.Select(d => d.CorrelationId).ToList();

        Assert.Equal(a1, a2);          // reproducible for audit
        Assert.NotEqual(a1, b);        // a different seed draws a different sample
    }

    [Fact]
    public void QuietPeriod_StillSpotChecksAtLeastOne()
    {
        var sampler = new RetrospectiveInspectionSampler(clock: () => Day0);
        var sample = sampler.SamplePeriodic(Decisions(autoProceeded: 3, escalated: 0), seed: 1);
        Assert.Single(sample.Decisions); // ceil(3 * 0.05) clamped to >= 1
    }

    [Fact]
    public void DailyNorthStar_DrawsFixedSizeFromTheDay()
    {
        var sampler = new RetrospectiveInspectionSampler(clock: () => Day0);
        var sample = sampler.DailyNorthStar(Decisions(autoProceeded: 40, escalated: 0),
            DateOnly.FromDateTime(Day0.UtcDateTime), seed: 3);

        Assert.Equal(InspectionReason.DailyNorthStar, sample.Reason);
        Assert.Equal(InspectionSamplerConfig.Default.DailyNorthStarSize, sample.Decisions.Count);
    }

    [Fact]
    public void EmptyPopulation_YieldsEmptySample()
    {
        var sampler = new RetrospectiveInspectionSampler(clock: () => Day0);
        var sample = sampler.SamplePeriodic(Decisions(autoProceeded: 0, escalated: 10), seed: 1);
        Assert.Empty(sample.Decisions);
    }
}
