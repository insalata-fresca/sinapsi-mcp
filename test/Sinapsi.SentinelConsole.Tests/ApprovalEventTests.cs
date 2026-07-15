using Sinapsi.SentinelConsole;
using Xunit;

namespace Sinapsi.SentinelConsole.Tests;

public sealed class ApprovalEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_RequestedFact_ProvenanceOnly_NoFreeText()
    {
        var json = """
        {"specversion":"1.0","type":"x","source":"approval-bridge-broker://shadow",
         "time":"2026-07-15T09:41:22.000000Z",
         "data":{"layer":"bridge","question":"operator-approval","surface":"approval-bridge",
                 "action_id":"garmin.oauth.exchange","target":"ct199-garmin (garmin-connector)",
                 "requester":"agent:worker/session-3","approver":"","verdict":"requested",
                 "reason":"request recorded; awaiting operator",
                 "params_digest":"9f2a1c4e7b80aa11", "result_status":null,
                 "correlation_id":"req-abc123"}}
        """;
        var e = ApprovalEvent.TryParse("homelab.security.approval.garmin.oauth.exchange.requested", json, Now);
        Assert.NotNull(e);
        Assert.Equal("garmin.oauth.exchange", e!.ActionId);
        Assert.Equal("requested", e.Verdict);
        Assert.Equal("agent:worker/session-3", e.Requester);
        Assert.Equal("", e.Approver);
        Assert.Equal("req-abc123", e.CorrelationId);
        Assert.Equal("9f2a1c4e7b80aa11", e.ParamsDigest);
        Assert.Equal("", e.ResultStatus);
        Assert.Equal(2026, e.Time.Year);
        Assert.Equal(9, e.Time.Hour);              // time from the envelope, not `now`
    }

    [Fact]
    public void Parses_ApprovedFact_CarriesApprover()
    {
        var json = """
        {"time":"2026-07-15T09:42:00Z",
         "data":{"layer":"bridge","action_id":"garmin.oauth.exchange","target":"ct199-garmin (garmin-connector)",
                 "requester":"agent:worker/session-3","approver":"operator:stefano","verdict":"approved",
                 "reason":"nonce consumed (one-shot)","params_digest":"9f2a1c4e7b80aa11",
                 "result_status":null,"correlation_id":"req-abc123"}}
        """;
        var e = ApprovalEvent.TryParse("homelab.security.approval.garmin.oauth.exchange.approved", json, Now);
        Assert.NotNull(e);
        Assert.Equal("approved", e!.Verdict);
        Assert.Equal("operator:stefano", e.Approver);
    }

    [Fact]
    public void Parses_ExecutedFact_CarriesResultStatus_NeverASecret()
    {
        var json = """
        {"time":"2026-07-15T09:42:05Z",
         "data":{"action_id":"garmin.oauth.exchange","target":"ct199-garmin (garmin-connector)",
                 "requester":"agent:worker/session-3","approver":"operator:stefano","verdict":"executed",
                 "reason":"executor accepted; ran under target identity","params_digest":"9f2a1c4e7b80aa11",
                 "result_status":"ok","correlation_id":"req-abc123"}}
        """;
        var e = ApprovalEvent.TryParse("homelab.security.approval.garmin.oauth.exchange.executed", json, Now);
        Assert.NotNull(e);
        Assert.Equal("executed", e!.Verdict);
        Assert.Equal("ok", e.ResultStatus);
    }

    [Fact]
    public void UnrelatedSubject_IsDropped()
        => Assert.Null(ApprovalEvent.TryParse(
            "homelab.security.authz.q2.allow.cse",
            """{"data":{"verdict":"allow","action_id":"x","correlation_id":"c"}}""", Now));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"data":null}""")]
    [InlineData("""{"data":{"action_id":"a"}}""")]                          // missing verdict
    [InlineData("""{"data":{"verdict":"requested"}}""")]                    // missing action_id
    [InlineData("""{"data":{"verdict":"requested","action_id":"a"}}""")]    // missing correlation_id — unjoinable
    public void UnparseableOrIncomplete_IsDropped(string json)
        => Assert.Null(ApprovalEvent.TryParse("homelab.security.approval.a.requested", json, Now));

    [Fact]
    public void MissingTime_FallsBackToNow()
    {
        var e = ApprovalEvent.TryParse(
            "homelab.security.approval.a.requested",
            """{"data":{"verdict":"requested","action_id":"a","correlation_id":"c1"}}""", Now);
        Assert.NotNull(e);
        Assert.Equal(Now, e!.Time);
    }
}
