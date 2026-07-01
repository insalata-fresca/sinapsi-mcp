using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sinapsi.AgentJwt;
using Sinapsi.Mcp;
using Xunit;

namespace SageCouncil.Mcp.Tests;

// -----------------------------------------------------------------------------
// LOAD-BEARING hardening leg (mirrors StepCa's SubprocessToolErrorTests): a
// misbehaving upstream emits a SECRET in its failure text, and we assert the tool
// surfaces "[redacted]" rather than the raw secret — proving the sanitizer is
// actually wired into the surfaced error path end-to-end, not just unit-tested in
// isolation. A second leg proves the per-member deadline timeout actually fires
// (and its message is sanitized).
//
// The fake handler fronts the OIDC token endpoint (so the real AgentJwtMinter
// mints against an on-disk RSA JWK) and the MCP gateway (so the real
// GatewayMcpClient drives the gemini spawn+poll). No live backend is touched.
// -----------------------------------------------------------------------------

public sealed class CouncilServiceErrorScrubTests : IDisposable
{
    private readonly string _keyDir;
    private readonly RSA _rsa;

    public CouncilServiceErrorScrubTests()
    {
        _keyDir = Path.Combine(Path.GetTempPath(), "council-scrub-jwk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_keyDir);
        _rsa = RSA.Create(2048);
        foreach (var agent in new[] { "agent-council-claude", "agent-council-gemini", "agent-council-chatgpt" })
        {
            var jwk = new
            {
                keyId = "kid-" + agent,
                userId = "user-" + agent,
                key = _rsa.ExportPkcs8PrivateKeyPem(),
                type = "serviceaccount",
            };
            File.WriteAllText(Path.Combine(_keyDir, $"{agent}.json"), JsonSerializer.Serialize(jwk));
        }
    }

    public void Dispose()
    {
        _rsa.Dispose();
        try { Directory.Delete(_keyDir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly Uri Backend = new("http://backend.test:8088");
    private static readonly Uri Gateway = new("http://gw.test:8443/mcp");

    private CouncilOptions Options(TimeSpan? memberDeadline = null) => new()
    {
        BackendUrl = Backend,
        GatewayUrl = Gateway,
        Timeout = TimeSpan.FromSeconds(10),
        MemberDeadline = memberDeadline ?? TimeSpan.FromSeconds(10),
    };

    private AgentJwtOptions JwtOptions() => new()
    {
        KeyDir = _keyDir,
        Issuer = "https://id.test",
        AudienceProjectId = "proj-123",
    };

    private CouncilService NewService(ScrubHandler handler, CouncilOptions? opt = null)
    {
        opt ??= Options();
        var minter = new AgentJwtMinter(new HttpClient(handler, disposeHandler: false), JwtOptions());
        var gateway = new GatewayMcpClient(new HttpClient(handler, disposeHandler: false));
        var http = new HttpClient(handler, disposeHandler: false);
        return new CouncilService(http, gateway, minter, opt, NullLogger<CouncilService>.Instance)
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
        };
    }

    private static JsonElement Council(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement MemberByName(JsonElement council, string name) =>
        council.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("member").GetString() == name);

    // -------------------------------------------------------------------- tests

    [Fact]
    public async Task A_gemini_failure_carrying_a_secret_is_redacted_in_the_member_error()
    {
        // The gemini poll flips to `failed` with an error string that embeds a
        // credential. CouncilService surfaces it through the member Error field —
        // it MUST be scrubbed. This is the load-bearing proof the sanitizer is on
        // the surfaced error path, not just unit-tested.
        var handler = new ScrubHandler
        {
            GeminiTaskId = "task-secret",
            GeminiStatusSequence = new[]
            {
                """{"status":"running"}""",
                """{"status":"failed","error":"auth rejected: token=leak-me-9f8a7b6c bearer secret"}""",
            },
        };
        var svc = NewService(handler);

        var council = Council(await svc.ConsultAsync(
            "q", "general", new[] { "gemini-research" }, CancellationToken.None));

        var err = MemberByName(council, "gemini-research").GetProperty("error").GetString()!;
        // Diagnostic context survives…
        Assert.Contains("gemini research failed", err);
        // …but the secret token value is gone, replaced by the placeholder.
        Assert.DoesNotContain("leak-me-9f8a7b6c", err);
        Assert.Contains("[redacted]", err);
    }

    [Fact]
    public async Task A_member_deadline_timeout_actually_fires_and_is_sanitized()
    {
        // The gateway stalls past the (tiny) member deadline; WithDeadlineAsync must
        // surface a sanitized deadline error rather than hang.
        var handler = new ScrubHandler { GatewayDelay = TimeSpan.FromSeconds(30) };
        var svc = NewService(handler, Options(memberDeadline: TimeSpan.FromMilliseconds(150)));

        var council = Council(await svc.ConsultAsync(
            "q", "general", new[] { "gemini-research" }, CancellationToken.None));

        var err = MemberByName(council, "gemini-research").GetProperty("error").GetString()!;
        Assert.Contains("member deadline exceeded", err);
        Assert.Contains("synthesis skipped", council.GetProperty("synthesis").GetString());
    }

    // ---------------------------------------------------------------- the router

    private sealed class ScrubHandler : HttpMessageHandler
    {
        public string GeminiTaskId { get; set; } = "task-1";
        public string[] GeminiStatusSequence { get; set; } = { """{"status":"done","result":"gemini"}""" };
        public TimeSpan GatewayDelay { get; set; } = TimeSpan.Zero;

        private int _statusIndex;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

            if (path.EndsWith("/oauth/v2/token", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"access_token":"fake-token","token_type":"Bearer","expires_in":900}""");

            if (uri.Host == Gateway.Host)
            {
                if (GatewayDelay > TimeSpan.Zero) await Task.Delay(GatewayDelay, ct);

                if (body.Contains("\"method\":\"initialize\""))
                {
                    var res = Json(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":1,"result":{}}""");
                    res.Headers.TryAddWithoutValidation("Mcp-Session-Id", "mcp-sess-1");
                    return res;
                }
                if (body.Contains("notifications/initialized"))
                    return new HttpResponseMessage(HttpStatusCode.Accepted);

                if (body.Contains("\"method\":\"tools/call\""))
                {
                    var tool = ExtractToolName(body);
                    var text = tool switch
                    {
                        "gemini_research" => $$"""{"task_id":"{{GeminiTaskId}}"}""",
                        "gemini_get_status" => NextGeminiStatus(),
                        _ => "",
                    };
                    return Json(HttpStatusCode.OK, McpContent(text));
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"unrouted {path}") };
        }

        private string NextGeminiStatus()
        {
            var i = Math.Min(_statusIndex, GeminiStatusSequence.Length - 1);
            _statusIndex++;
            return GeminiStatusSequence[i];
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
            new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static string McpContent(string text) =>
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                result = new { content = new[] { new { type = "text", text } } },
            });

        private static string ExtractToolName(string body)
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("params").GetProperty("name").GetString() ?? "";
        }
    }
}
