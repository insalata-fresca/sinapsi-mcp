using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ApprovalBridge.Mcp.Tests;

/// <summary>
/// End-to-end tool behaviour against a scripted broker fake — the same "recording fake" pattern
/// used for the sibling security-adjacent tools (<c>InfisicalToolsTests</c>). Covers: the pending
/// handle shape on success, clean mapping of every broker denial, malformed-call rejection before
/// any network call, and — the CARD's explicit acceptance bar — that the tool's result NEVER
/// claims an approval, no matter what the broker returns.
/// </summary>
public sealed class ApprovalBridgeToolsTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body)> Calls { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = """{"request_id":"req-abc"}""";

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

    private static (ApprovalBridgeTools tools, ScriptedHandler rec) Build(ScriptedHandler? rec = null)
    {
        rec ??= new ScriptedHandler();
        var opt = new ApprovalBridgeOptions
        {
            BrokerBaseUrl = "http://broker.example.org:8013",
            RequesterIdentity = "agent:cervello-worker/session-7",
        };
        var client = new ApprovalBridgeClient(new HttpClient(rec), opt);
        return (new ApprovalBridgeTools(client), rec);
    }

    private static JsonElement Parse(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public async Task AcceptedRequest_ReturnsAPendingHandle_NeverAnApproval()
    {
        var (tools, rec) = Build();

        var resultJson = await tools.approval_bridge_request(
            "garmin.oauth.exchange", """{"auth_code":"abcd1234"}""", CancellationToken.None);
        var result = Parse(resultJson);

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("pending", result.GetProperty("status").GetString());
        Assert.Equal("req-abc", result.GetProperty("request_id").GetString());
        Assert.Equal("garmin.oauth.exchange", result.GetProperty("action_id").GetString());

        // The message must be explicit that this is NOT an approval and nothing ran.
        var message = result.GetProperty("message").GetString()!;
        Assert.Contains("NOT an approval", message);
        Assert.DoesNotContain("approved", message, StringComparison.OrdinalIgnoreCase);

        // The response never carries an "approved" or "executed" field of any kind.
        Assert.False(result.TryGetProperty("approved", out _));
        Assert.False(result.TryGetProperty("executed", out _));
        Assert.False(result.TryGetProperty("result", out _));

        // Exactly one call, to the broker's /request endpoint (never /approve or /reject).
        var call = Assert.Single(rec.Calls);
        Assert.EndsWith("/request", call.Url);
    }

    [Fact]
    public async Task BrokerRejection_UnknownAction_IsMappedCleanly()
    {
        var (tools, _) = Build(new ScriptedHandler
        {
            Status = HttpStatusCode.UnprocessableEntity,
            ResponseBody = """{"rejected":"UnknownAction"}""",
        });

        var result = Parse(await tools.approval_bridge_request("not.a.real.action", "{}", CancellationToken.None));

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("UnknownAction", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task BrokerRejection_ParamsSchemaViolation_IsMappedCleanly()
    {
        var (tools, _) = Build(new ScriptedHandler
        {
            Status = HttpStatusCode.UnprocessableEntity,
            ResponseBody = """{"rejected":"ParamsSchemaViolation"}""",
        });

        var result = Parse(await tools.approval_bridge_request(
            "garmin.oauth.exchange", """{"auth_code":"short"}""", CancellationToken.None));

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("ParamsSchemaViolation", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task BrokerRejection_RateLimited_IsMappedCleanly()
    {
        var (tools, _) = Build(new ScriptedHandler
        {
            Status = HttpStatusCode.UnprocessableEntity,
            ResponseBody = """{"rejected":"RateLimited"}""",
        });

        var result = Parse(await tools.approval_bridge_request("garmin.oauth.exchange", "{}", CancellationToken.None));

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("RateLimited", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MalformedActionId_IsRejected_BeforeAnyBrokerCall()
    {
        var (tools, rec) = Build();

        var result = Parse(await tools.approval_bridge_request("", "{}", CancellationToken.None));

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("action_id is required", result.GetProperty("error").GetString());
        Assert.Empty(rec.Calls); // deny-by-default before any network call
    }

    [Fact]
    public async Task MalformedParams_IsRejected_BeforeAnyBrokerCall()
    {
        var (tools, rec) = Build();

        var result = Parse(await tools.approval_bridge_request("garmin.oauth.exchange", "not-json", CancellationToken.None));

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("params is not valid JSON", result.GetProperty("error").GetString());
        Assert.Empty(rec.Calls);
    }

    [Fact]
    public async Task NullParams_DefaultsToEmptyObject_AndSucceeds()
    {
        var (tools, rec) = Build();

        var result = Parse(await tools.approval_bridge_request("garmin.oauth.exchange", null, CancellationToken.None));

        Assert.True(result.GetProperty("ok").GetBoolean());
        var call = Assert.Single(rec.Calls);
        var sentParams = Parse(call.Body).GetProperty("params").GetString();
        Assert.Equal("{}", sentParams);
    }

    [Fact]
    public async Task TransportFailure_IsCaughtAndSanitized_NotAnUnhandledFault()
    {
        var opt = new ApprovalBridgeOptions
        {
            BrokerBaseUrl = "http://broker.example.org:8013",
            RequesterIdentity = "agent:cervello-worker/session-7",
        };
        var throwingHandler = new ThrowingHandler();
        var client = new ApprovalBridgeClient(new HttpClient(throwingHandler), opt);
        var tools = new ApprovalBridgeTools(client);

        var result = Parse(await tools.approval_bridge_request("garmin.oauth.exchange", "{}", CancellationToken.None));

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("error").GetString()));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused: password=should-never-leak");
    }
}
