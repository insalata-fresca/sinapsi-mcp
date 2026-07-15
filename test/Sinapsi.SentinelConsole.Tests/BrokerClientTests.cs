using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sinapsi.SentinelConsole;
using Xunit;

namespace Sinapsi.SentinelConsole.Tests;

/// <summary>
/// The Console's ONLY path to the broker (E1.7): a thin, transparent proxy. These tests prove it
/// (a) forwards Approve/Reject verbatim — status code and body untouched, no reinterpretation of the
/// broker's verdict — and (b) fails soft (never throws) when the broker is unreachable, rather than
/// ever letting the Console fabricate an "approved" outcome the broker never issued.
/// </summary>
public sealed class BrokerClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return _respond(request);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }

    private static BrokerClient Client(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://broker.local:8013") };
        return new BrokerClient(http, NullLogger<BrokerClient>.Instance);
    }

    [Fact]
    public async Task GetPendingAsync_ParsesTitleTypedParamsAndProvenance()
    {
        var body = """
        [{"requestId":"req-1","actionId":"garmin.oauth.exchange","title":"Garmin OAuth code→token exchange",
          "description":"Exchange a code for a token.","params":{"auth_code":"abcd1234efgh"},
          "requesterIdentity":"agent:worker/session-3","expiresAt":"2026-07-15T09:46:22Z","riskTier":"yellow"}]
        """;
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        var result = await Client(handler).GetPendingAsync();

        Assert.True(result.BrokerReachable);
        var item = Assert.Single(result.Items);
        Assert.Equal("req-1", item.RequestId);
        Assert.Equal("Garmin OAuth code→token exchange", item.Title);
        Assert.Equal("agent:worker/session-3", item.RequesterIdentity);
        Assert.Equal("yellow", item.RiskTier);
        Assert.NotNull(item.Params);
        Assert.Equal("abcd1234efgh", item.Params!.Value.GetProperty("auth_code").GetString());
    }

    [Fact]
    public async Task GetPendingAsync_NonSuccessStatus_ReportsUnreachable_NotACrash()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var result = await Client(handler).GetPendingAsync();

        Assert.False(result.BrokerReachable);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetPendingAsync_ConnectionFailure_ReportsUnreachable_NeverThrows()
    {
        var result = await Client(new ThrowingHandler()).GetPendingAsync();

        Assert.False(result.BrokerReachable);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ApproveAsync_ForwardsRequestIdAndOperatorIdentity_ToTheCommandEndpoint()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"dispatched":true,"executor_accepted":false,"detail":"deny-by-default"}""") });

        var result = await Client(handler).ApproveAsync("req-1", "operator:console");

        Assert.True(result.Reached);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("dispatched", result.RawBody);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/approve", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("\"request_id\":\"req-1\"", handler.LastRequestBody);
        Assert.Contains("\"approver_identity\":\"operator:console\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task ApproveAsync_BrokerRejection_IsForwardedVerbatim_NotSwallowed()
    {
        // The broker's own self-approval / one-shot / CAS checks reject — the Console must relay
        // that exactly, never translate a 409 into a false "ok".
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        { Content = new StringContent("""{"rejected":"SelfApproval"}""") });

        var result = await Client(handler).ApproveAsync("req-1", "agent:worker/session-3");

        Assert.True(result.Reached);
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("SelfApproval", result.RawBody);
    }

    [Fact]
    public async Task RejectAsync_PostsToRejectEndpoint()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("""{"rejected":true}""") });

        var result = await Client(handler).RejectAsync("req-9", "operator:console");

        Assert.True(result.Reached);
        Assert.Equal("/reject", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("\"request_id\":\"req-9\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task ApproveAsync_BrokerUnreachable_NeverThrows_ReportsUnreached()
    {
        var result = await Client(new ThrowingHandler()).ApproveAsync("req-1", "operator:console");

        Assert.False(result.Reached);
    }
}
