using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sinapsi.AgentJwt;
using Sinapsi.Mcp;
using Xunit;

namespace SageCouncil.Mcp.Tests;

/// <summary>
/// Real orchestration tests for <see cref="CouncilService.ConsultAsync"/>. They
/// inject a fake <see cref="HttpMessageHandler"/> that simultaneously stands in
/// for (a) the OIDC token endpoint (so the real <see cref="AgentJwtMinter"/>
/// mints against an on-disk RSA JWK), (b) the agent backend the claude member +
/// the synthesis pass spawn sessions on, and (c) the MCP gateway the gemini /
/// chatgpt members call through (driven by the real <see cref="GatewayMcpClient"/>).
///
/// The earlier port only covered the unknown-member short-circuit; these cover
/// the live fan-out + synthesis paths the parity mandate calls out: a 2-member
/// prose synthesis, a single-usable-member passthrough, the design-focus
/// structured-JSON merge, a per-member deadline timeout, and the gemini poll
/// done / failed transitions.
/// </summary>
public sealed class CouncilServiceTests : IDisposable
{
    private readonly string _keyDir;
    private readonly RSA _rsa;

    public CouncilServiceTests()
    {
        _keyDir = Path.Combine(Path.GetTempPath(), "council-jwk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_keyDir);
        _rsa = RSA.Create(2048);
        // One JWK per default council identity (claude / gemini / chatgpt).
        foreach (var agent in new[] { "agent-council-claude", "agent-council-gemini", "agent-council-chatgpt" })
            WriteJwk(agent);
    }

    private void WriteJwk(string agent)
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

    public void Dispose()
    {
        _rsa.Dispose();
        try { Directory.Delete(_keyDir, recursive: true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------ harness

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

    /// <summary>
    /// Build a CouncilService over a single fake handler that fronts the token
    /// endpoint, the agent backend, and the MCP gateway. The handler is shared by
    /// the minter, the council's own HttpClient, and the GatewayMcpClient so one
    /// router serves every outbound call.
    /// </summary>
    private CouncilService NewService(RoutingHandler handler, CouncilOptions? opt = null, TimeSpan? pollInterval = null)
    {
        opt ??= Options();
        var minter = new AgentJwtMinter(new HttpClient(handler, disposeHandler: false), JwtOptions());
        var gateway = new GatewayMcpClient(new HttpClient(handler, disposeHandler: false));
        var http = new HttpClient(handler, disposeHandler: false);
        return new CouncilService(http, gateway, minter, opt, NullLogger<CouncilService>.Instance)
        {
            // Poll fast in tests; production stays at 10s.
            PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(20),
        };
    }

    // ---------------------------------------------------------- assertion helpers

    private static JsonElement Council(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement MemberByName(JsonElement council, string name) =>
        council.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("member").GetString() == name);

    // -------------------------------------------------------------------- tests

    [Fact]
    public async Task Two_members_produce_a_prose_synthesis_via_the_agent_backend()
    {
        // claude (agent backend) + chatgpt (gateway codex) both answer; the
        // synthesis is a 4th agent-backend session whose reply we control.
        var handler = new RoutingHandler
        {
            ClaudeReport = "claude says X",
            CodexReport = "chatgpt says Y",
            SynthesisReport = "RECONCILED: X and Y agree on the core point.",
        };
        var svc = NewService(handler);

        var council = Council(await svc.ConsultAsync(
            "a hard question", "general",
            new[] { "claude-research", "chatgpt-research" }, CancellationToken.None));

        Assert.Equal("claude says X", MemberByName(council, "claude-research").GetProperty("report").GetString());
        Assert.Equal("chatgpt says Y", MemberByName(council, "chatgpt-research").GetProperty("report").GetString());
        Assert.Equal(JsonValueKind.Null, MemberByName(council, "claude-research").GetProperty("error").ValueKind);
        Assert.Equal("RECONCILED: X and Y agree on the core point.", council.GetProperty("synthesis").GetString());

        // The synthesis instruction for a non-structured focus must ask for prose,
        // not a JSON merge.
        Assert.Contains("2-3 paragraph", handler.LastSynthesisPrompt);
        Assert.DoesNotContain("merged JSON", handler.LastSynthesisPrompt);
        // Two distinct synthesis perspectives were handed in.
        Assert.Contains("Perspective 1", handler.LastSynthesisPrompt);
        Assert.Contains("Perspective 2", handler.LastSynthesisPrompt);
    }

    [Fact]
    public async Task A_single_usable_member_passes_straight_through_without_a_synthesis_call()
    {
        // Only claude is usable; the synthesis pass must be SKIPPED (the lone
        // report passes through verbatim) — so the backend sees no 2nd session.
        var handler = new RoutingHandler { ClaudeReport = "the only answer" };
        var svc = NewService(handler);

        var council = Council(await svc.ConsultAsync(
            "q", "general", new[] { "claude-research" }, CancellationToken.None));

        Assert.Equal("the only answer", council.GetProperty("synthesis").GetString());
        Assert.Equal("the only answer", MemberByName(council, "claude-research").GetProperty("report").GetString());
        // Exactly one /v1/sessions create (the member), none for synthesis.
        Assert.Equal(1, handler.SessionCreateCount);
        Assert.Null(handler.LastSynthesisPrompt);
    }

    [Fact]
    public async Task Design_focus_makes_the_synthesis_a_structured_json_merge()
    {
        var handler = new RoutingHandler
        {
            ClaudeReport = """{"stages":["a"]}""",
            CodexReport = """{"stages":["b"]}""",
            SynthesisReport = """{"stages":["a","b"]}""",
        };
        var svc = NewService(handler);

        var council = Council(await svc.ConsultAsync(
            "design step", "design",
            new[] { "claude-research", "chatgpt-research" }, CancellationToken.None));

        Assert.Equal("""{"stages":["a","b"]}""", council.GetProperty("synthesis").GetString());
        // The design focus must drive the JSON-merge instruction, not the prose one.
        Assert.Contains("merged JSON", handler.LastSynthesisPrompt);
        Assert.Contains("Output ONLY the merged JSON", handler.LastSynthesisPrompt);
        Assert.DoesNotContain("2-3 paragraph", handler.LastSynthesisPrompt);
    }

    [Fact]
    public async Task A_member_that_exceeds_its_deadline_is_reported_as_a_timeout_error()
    {
        // The agent backend stalls past the member deadline; WithDeadlineAsync must
        // surface a deadline error instead of hanging. The lone member is then
        // unusable → synthesis is skipped.
        var handler = new RoutingHandler { MessageDelay = TimeSpan.FromSeconds(30), ClaudeReport = "too late" };
        var svc = NewService(handler, Options(memberDeadline: TimeSpan.FromMilliseconds(150)));

        var council = Council(await svc.ConsultAsync(
            "q", "general", new[] { "claude-research" }, CancellationToken.None));

        var member = MemberByName(council, "claude-research");
        Assert.Equal("", member.GetProperty("report").GetString());
        Assert.Contains("member deadline exceeded", member.GetProperty("error").GetString());
        Assert.Contains("synthesis skipped", council.GetProperty("synthesis").GetString());
    }

    [Fact]
    public async Task Gemini_poll_returns_the_result_text_on_a_done_transition()
    {
        // gemini_research spawns a task_id, then gemini_get_status reports
        // running twice before flipping to done with the final text.
        var handler = new RoutingHandler
        {
            GeminiTaskId = "task-42",
            GeminiStatusSequence = new[]
            {
                """{"status":"running"}""",
                """{"status":"running"}""",
                """{"status":"done","result":"gemini deep-research findings"}""",
            },
        };
        var svc = NewService(handler);

        var council = Council(await svc.ConsultAsync(
            "q", "general", new[] { "gemini-research" }, CancellationToken.None));

        var member = MemberByName(council, "gemini-research");
        Assert.Equal("gemini deep-research findings", member.GetProperty("report").GetString());
        Assert.Equal(JsonValueKind.Null, member.GetProperty("error").ValueKind);
        // Single usable member → passthrough synthesis.
        Assert.Equal("gemini deep-research findings", council.GetProperty("synthesis").GetString());
        Assert.True(handler.GeminiStatusPolls >= 3);
    }

    [Fact]
    public async Task Gemini_poll_surfaces_a_failed_transition_as_a_member_error()
    {
        var handler = new RoutingHandler
        {
            GeminiTaskId = "task-99",
            GeminiStatusSequence = new[]
            {
                """{"status":"running"}""",
                """{"status":"failed","error":"upstream model unavailable"}""",
            },
        };
        var svc = NewService(handler);

        var council = Council(await svc.ConsultAsync(
            "q", "general", new[] { "gemini-research" }, CancellationToken.None));

        var member = MemberByName(council, "gemini-research");
        Assert.Equal("", member.GetProperty("report").GetString());
        Assert.Contains("gemini research failed", member.GetProperty("error").GetString());
        Assert.Contains("upstream model unavailable", member.GetProperty("error").GetString());
        // No usable member → explicit skip sentinel.
        Assert.Contains("synthesis skipped", council.GetProperty("synthesis").GetString());
    }

    // ---------------------------------------------------------------- the router

    /// <summary>
    /// A single <see cref="HttpMessageHandler"/> that routes by URI: the OIDC
    /// token endpoint, the agent backend (session create / message / delete) and
    /// the MCP gateway (initialize → notifications/initialized → tools/call,
    /// including the gemini two-phase spawn+poll). State is captured for asserts.
    /// </summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        // Backend member + synthesis replies.
        public string ClaudeReport { get; set; } = "";
        public string SynthesisReport { get; set; } = "synthesised";
        public TimeSpan MessageDelay { get; set; } = TimeSpan.Zero;

        // Gateway members.
        public string CodexReport { get; set; } = "";
        public string GeminiTaskId { get; set; } = "task-1";
        public string[] GeminiStatusSequence { get; set; } = { """{"status":"done","result":"gemini"}""" };

        // Captured state for assertions.
        public int SessionCreateCount;
        public int GeminiStatusPolls;
        public string? LastSynthesisPrompt;

        private int _statusIndex;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

            // --- OIDC token endpoint (used by the real AgentJwtMinter) ---
            if (path.EndsWith("/oauth/v2/token", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"access_token":"fake-token","token_type":"Bearer","expires_in":900}""");

            // --- agent backend (claude member + synthesis) ---
            if (uri.Host == Backend.Host)
            {
                if (request.Method == HttpMethod.Post && path == "/v1/sessions")
                {
                    Interlocked.Increment(ref SessionCreateCount);
                    return Json(HttpStatusCode.OK, $$"""{"session_id":"sess-{{Guid.NewGuid():N}}"}""");
                }
                if (request.Method == HttpMethod.Post && path.EndsWith("/messages", StringComparison.Ordinal))
                {
                    if (MessageDelay > TimeSpan.Zero) await Task.Delay(MessageDelay, ct);
                    // Distinguish a member call from the synthesis call: the synthesis
                    // prompt carries the reconciliation instruction.
                    var msg = ExtractMessage(body);
                    if (msg.Contains("expert perspectives") || msg.Contains("expert analyses"))
                    {
                        LastSynthesisPrompt = msg;
                        return Json(HttpStatusCode.OK, JsonResult(SynthesisReport));
                    }
                    return Json(HttpStatusCode.OK, JsonResult(ClaudeReport));
                }
                if (request.Method == HttpMethod.Delete)
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            // --- MCP gateway (gemini / chatgpt) ---
            if (uri.Host == Gateway.Host)
            {
                // initialize → hand back an Mcp-Session-Id header so GatewayMcpClient proceeds.
                if (body.Contains("\"method\":\"initialize\""))
                {
                    var res = Json(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":1,"result":{}}""");
                    res.Headers.TryAddWithoutValidation("Mcp-Session-Id", "mcp-sess-1");
                    return res;
                }
                // notifications/initialized → 202, no body.
                if (body.Contains("notifications/initialized"))
                    return new HttpResponseMessage(HttpStatusCode.Accepted);

                // tools/call → wrap the tool output as MCP content text.
                if (body.Contains("\"method\":\"tools/call\""))
                {
                    var tool = ExtractToolName(body);
                    var text = tool switch
                    {
                        "gemini_research" => $$"""{"task_id":"{{GeminiTaskId}}"}""",
                        "gemini_get_status" => NextGeminiStatus(),
                        "codex_codex" => CodexReport,
                        _ => "",
                    };
                    return Json(HttpStatusCode.OK, McpContent(text));
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"unrouted {path}") };
        }

        private string NextGeminiStatus()
        {
            Interlocked.Increment(ref GeminiStatusPolls);
            var i = Math.Min(_statusIndex, GeminiStatusSequence.Length - 1);
            _statusIndex++;
            return GeminiStatusSequence[i];
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
            new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        // Agent-backend message reply shape: {"result":"..."}.
        private static string JsonResult(string text) =>
            JsonSerializer.Serialize(new { result = text });

        // MCP tool result shape: {"result":{"content":[{"type":"text","text":"..."}]}}.
        private static string McpContent(string text) =>
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                result = new { content = new[] { new { type = "text", text } } },
            });

        private static string ExtractMessage(string body)
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        }

        private static string ExtractToolName(string body)
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("params").GetProperty("name").GetString() ?? "";
        }
    }
}
