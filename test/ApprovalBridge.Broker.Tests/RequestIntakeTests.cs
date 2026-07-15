using ApprovalBridge.Broker.Model;
using Xunit;

namespace ApprovalBridge.Broker.Tests;

/// <summary>
/// REQUEST intake (docs/66 §3.1, I6). Validate <c>action_id</c> against the allowlist and params against
/// the action's <c>param_schema</c>; refuse malformed requests immediately (deny-by-default). The
/// executor only ever sees <c>action_id</c> + schema-validated params — never free text.
/// </summary>
public sealed class RequestIntakeTests
{
    private const string Requester = "agent:worker/session-3";

    [Fact]
    public async Task ValidRequest_IsAccepted_AndEmitsRequestedWithParamsDigest()
    {
        var h = BrokerFixture.Build();
        var o = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);

        Assert.True(o.Accepted);
        Assert.NotEmpty(o.RequestId);
        var requested = Assert.Single(h.Emitter.Facts);
        Assert.Equal("requested", requested.Verdict);
        Assert.Equal(o.RequestId, requested.CorrelationId);
        // The audit carries a params digest, never the raw params.
        var digest = requested.Envelope["params_digest"]!.GetValue<string>();
        Assert.Matches("^[0-9a-f]{64}$", digest);
    }

    [Theory]
    [InlineData("""{ "auth_code": "short" }""")]                       // minLength 8 violated
    [InlineData("""{ "auth_code": "abcd1234", "extra": "x" }""")]      // additionalProperties:false
    [InlineData("""{ }""")]                                            // required auth_code missing
    [InlineData("not-json")]                                            // unparseable
    [InlineData("""{ "auth_code": 12345678 }""")]                     // wrong type
    public async Task ParamsFailingSchema_AreRejected_DenyByDefault(string badParams)
    {
        var h = BrokerFixture.Build();
        var o = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, badParams, Requester);

        Assert.False(o.Accepted);
        Assert.Equal(BrokerRejectReason.ParamsSchemaViolation, o.Reason);
        Assert.Empty(h.Emitter.Facts);
    }

    [Fact]
    public async Task RateLimit_PerAgent_IsEnforced()
    {
        // Demo action allows 3 per agent per hour.
        var h = BrokerFixture.Build();
        for (var i = 0; i < 3; i++)
            Assert.True((await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester)).Accepted);

        var fourth = await h.Broker.RequestAsync(BrokerFixture.DemoActionId, BrokerFixture.ValidParams, Requester);
        Assert.False(fourth.Accepted);
        Assert.Equal(BrokerRejectReason.RateLimited, fourth.Reason);
    }
}
