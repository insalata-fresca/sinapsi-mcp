using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ApprovalBridge.Mcp.Tests;

/// <summary>
/// The client's only network seam: <c>POST &lt;broker&gt;/request</c>. Pins the request body shape
/// (action_id/params/requester_identity, with requester_identity ALWAYS the configured deployment
/// identity, never caller-supplied), the accept path (2xx → request_id), and every denial path
/// (422 rejected reason, non-JSON error body, missing request_id on a 2xx response).
/// </summary>
public sealed class ApprovalBridgeClientTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body)> Calls { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = """{"request_id":"req-123"}""";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static ApprovalBridgeOptions Opt() => new()
    {
        BrokerBaseUrl = "http://broker.example.org:8013",
        RequesterIdentity = "agent:cervello-worker/session-7",
    };

    private static (ApprovalBridgeClient client, ScriptedHandler rec) Build(ScriptedHandler? rec = null)
    {
        rec ??= new ScriptedHandler();
        return (new ApprovalBridgeClient(new HttpClient(rec), Opt()), rec);
    }

    private static JsonElement Parse(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public async Task RequestAsync_PostsToBrokerRequestEndpoint_WithConfiguredRequesterIdentity()
    {
        var (client, rec) = Build();

        var result = await client.RequestAsync("garmin.oauth.exchange", """{"auth_code":"abcd1234"}""", CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal("req-123", result.RequestId);

        var call = Assert.Single(rec.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal("http://broker.example.org:8013/request", call.Url);

        var sent = Parse(call.Body);
        Assert.Equal("garmin.oauth.exchange", sent.GetProperty("action_id").GetString());
        Assert.Equal("""{"auth_code":"abcd1234"}""", sent.GetProperty("params").GetString());
        // requester_identity is ALWAYS the deployment's own configured identity — the caller
        // (the tool) never gets to pass one in.
        Assert.Equal("agent:cervello-worker/session-7", sent.GetProperty("requester_identity").GetString());
    }

    [Fact]
    public async Task RequestAsync_MapsA422RejectedBody_ToADenialWithTheBrokersReason()
    {
        var (client, _) = Build(new ScriptedHandler
        {
            Status = HttpStatusCode.UnprocessableEntity,
            ResponseBody = """{"rejected":"UnknownAction"}""",
        });

        var result = await client.RequestAsync("not.registered", "{}", CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("UnknownAction", result.DenialReason);
        Assert.Equal("", result.RequestId);
    }

    [Theory]
    [InlineData("ParamsSchemaViolation")]
    [InlineData("RateLimited")]
    public async Task RequestAsync_MapsEveryBrokerDenialReason(string reason)
    {
        var (client, _) = Build(new ScriptedHandler
        {
            Status = HttpStatusCode.UnprocessableEntity,
            ResponseBody = $$"""{"rejected":"{{reason}}"}""",
        });

        var result = await client.RequestAsync("garmin.oauth.exchange", "{}", CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal(reason, result.DenialReason);
    }

    [Fact]
    public async Task RequestAsync_ANonJsonErrorBody_StillYieldsACleanDenial_NotAThrow()
    {
        var (client, _) = Build(new ScriptedHandler
        {
            Status = HttpStatusCode.BadGateway,
            ResponseBody = "upstream proxy error",
        });

        var result = await client.RequestAsync("garmin.oauth.exchange", "{}", CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Contains("502", result.DenialReason);
    }

    [Fact]
    public async Task RequestAsync_A2xxResponseMissingRequestId_ThrowsRatherThanClaimingSuccessOrDenial()
    {
        // A 2xx with no request_id is a broken upstream CONTRACT, not a normal accept/deny outcome —
        // silently mapping it to either would be dishonest about what actually happened.
        var (client, _) = Build(new ScriptedHandler
        {
            Status = HttpStatusCode.OK,
            ResponseBody = """{"somethingElse":"x"}""",
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RequestAsync("garmin.oauth.exchange", "{}", CancellationToken.None));
        Assert.Contains("request_id", ex.Message);
    }
}
