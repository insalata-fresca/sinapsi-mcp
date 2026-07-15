using Sinapsi.Nats.EventPlane;
using Xunit;

namespace Sinapsi.Nats.Tests.EventPlane;

/// <summary>Source-of-truth reads (docs/64 §3): an enforcement decision may read trust-plane
/// state only from the authoritative store, never a lagging bus projection.</summary>
public sealed class TrustPlaneReadGuardTests
{
    [Fact]
    public void RequireAuthoritative_Throws_OnABusProjection()
    {
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => TrustPlaneReadGuard.RequireAuthoritative(TrustStateSource.BusProjection, "q1-can-call"));
        Assert.Contains("BUS PROJECTION", ex.Message);
        Assert.Contains("no safety property", ex.Message);
    }

    [Fact]
    public void RequireAuthoritative_Passes_ForTheAuthoritativeStore()
    {
        var ex = Record.Exception(
            () => TrustPlaneReadGuard.RequireAuthoritative(TrustStateSource.AuthoritativeStore, "q1-can-call"));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(TrustStateSource.AuthoritativeStore, true)]
    [InlineData(TrustStateSource.BusProjection, false)]
    public void IsAuthoritative_OnlyTheStoreIsSafeToEnforceOn(TrustStateSource source, bool expected)
        => Assert.Equal(expected, TrustPlaneReadGuard.IsAuthoritative(source));
}
