using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Sinapsi.Mcp;
using Xunit;

namespace Sinapsi.Mcp.Tests;

public class GatewayMcpClientTests
{
    private static readonly Uri Gateway = new("https://upstream.test/mcp");

    /// <summary>
    /// Scripts a sequence of responses for the initialize / notifications /
    /// tools-call round-trip and records every outbound request so tests can
    /// assert on headers and ordering.
    /// </summary>
    private sealed class ScriptedHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return responses.Dequeue();
        }
    }

    private static HttpResponseMessage Init(string sessionId)
    {
        var res = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        res.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        return res;
    }

    private static HttpResponseMessage Ok(string body, string contentType = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private static (GatewayMcpClient client, ScriptedHandler handler) Build(params HttpResponseMessage[] scripted)
    {
        var handler = new ScriptedHandler(new Queue<HttpResponseMessage>(scripted));
        var http = new HttpClient(handler);
        return (new GatewayMcpClient(http), handler);
    }

    [Fact]
    public async Task CallTool_extracts_text_from_plain_json_result()
    {
        var (client, handler) = Build(
            Init("sess-123"),
            Ok("{}"), // notifications/initialized
            Ok("""{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"hello"},{"type":"text","text":"world"}]}}"""));

        var result = await client.CallToolAsync(Gateway, "jwt", "echo", new { x = 1 }, CancellationToken.None);

        Assert.Equal("hello" + Environment.NewLine + "world", result);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task CallTool_extracts_text_from_sse_data_frame()
    {
        var sse = "event: message\n" +
                  "data: {\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"streamed\"}]}}\n\n";
        var (client, _) = Build(
            Init("sess-abc"),
            Ok("{}"),
            Ok(sse, "text/event-stream"));

        var result = await client.CallToolAsync(Gateway, "jwt", "echo", new { }, CancellationToken.None);

        Assert.Equal("streamed", result);
    }

    [Fact]
    public async Task CallTool_threads_session_id_and_bearer_on_later_requests()
    {
        var (client, handler) = Build(
            Init("the-session"),
            Ok("{}"),
            Ok("""{"result":{"content":[]}}"""));

        await client.CallToolAsync(Gateway, "my-token", "echo", new { }, CancellationToken.None);

        // initialize carries no session id; subsequent calls thread the captured one.
        Assert.False(handler.Requests[0].Headers.Contains("Mcp-Session-Id"));
        Assert.True(handler.Requests[1].Headers.TryGetValues("Mcp-Session-Id", out var s1));
        Assert.Equal("the-session", s1!.Single());
        Assert.True(handler.Requests[2].Headers.TryGetValues("Mcp-Session-Id", out var s2));
        Assert.Equal("the-session", s2!.Single());

        // bearer identity is forwarded on every request.
        foreach (var req in handler.Requests)
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "my-token"), req.Headers.Authorization);

        // the final tools/call names the requested tool.
        Assert.Contains("\"tools/call\"", handler.Bodies[2]);
        Assert.Contains("\"echo\"", handler.Bodies[2]);
    }

    [Fact]
    public async Task CallTool_throws_when_upstream_omits_session_id()
    {
        var noSession = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        var (client, _) = Build(noSession);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallToolAsync(Gateway, "jwt", "echo", new { }, CancellationToken.None));
        Assert.Contains("mcp-session-id", ex.Message);
    }

    [Fact]
    public async Task CallTool_throws_when_initialize_fails()
    {
        var bad = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("nope", Encoding.UTF8, "text/plain"),
        };
        var (client, _) = Build(bad);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallToolAsync(Gateway, "jwt", "echo", new { }, CancellationToken.None));
        Assert.Contains("initialize", ex.Message);
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task CallTool_throws_on_jsonrpc_error_payload()
    {
        var (client, _) = Build(
            Init("sess"),
            Ok("{}"),
            Ok("""{"jsonrpc":"2.0","id":2,"error":{"code":-32000,"message":"boom"}}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallToolAsync(Gateway, "jwt", "echo", new { }, CancellationToken.None));
        Assert.Contains("tool error", ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task CallTool_throws_on_http_error_from_tools_call()
    {
        var fail = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("upstream exploded", Encoding.UTF8, "text/plain"),
        };
        var (client, _) = Build(Init("sess"), Ok("{}"), fail);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CallToolAsync(Gateway, "jwt", "echo", new { }, CancellationToken.None));
        Assert.Contains("tools/call", ex.Message);
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task CallTool_returns_empty_when_result_has_no_content_array()
    {
        var (client, _) = Build(
            Init("sess"),
            Ok("{}"),
            Ok("""{"jsonrpc":"2.0","id":2,"result":{}}"""));

        var result = await client.CallToolAsync(Gateway, "jwt", "echo", new { }, CancellationToken.None);

        Assert.Equal(string.Empty, result);
    }
}
