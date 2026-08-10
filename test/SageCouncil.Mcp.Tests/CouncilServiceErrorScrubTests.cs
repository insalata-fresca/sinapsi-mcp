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
// mints against an on-disk RSA JWK) and the brain-api front door (so the gemini
// member drives its repointed agy session: create → /prompt → /events). No live
// backend is touched. Post-repoint the gemini member no longer calls the gateway.
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
    public async Task A_gemini_agy_failure_carrying_a_secret_is_redacted_in_the_member_error()
    {
        // The brain-api /prompt inject fails and the upstream body embeds a credential.
        // CouncilService surfaces the bounded body through the member Error field — it MUST
        // be scrubbed. This is the load-bearing proof the sanitizer is on the surfaced error
        // path (now the agy front-door path), not just unit-tested.
        var handler = new ScrubHandler
        {
            PromptFailureBody = "auth rejected: token=leak-me-9f8a7b6c bearer secret",
        };
        var svc = NewService(handler);

        var council = Council(await svc.ConsultAsync(
            "q", "general", new[] { "gemini-research" }, CancellationToken.None));

        var err = MemberByName(council, "gemini-research").GetProperty("error").GetString()!;
        // Diagnostic context survives…
        Assert.Contains("agy", err);
        // …but the secret token value is gone, replaced by the placeholder.
        Assert.DoesNotContain("leak-me-9f8a7b6c", err);
        Assert.Contains("[redacted]", err);
    }

    [Fact]
    public async Task A_member_deadline_timeout_actually_fires_and_is_sanitized()
    {
        // The brain-api backend stalls the /prompt inject past the (tiny) member deadline;
        // WithDeadlineAsync must surface a sanitized deadline error rather than hang.
        var handler = new ScrubHandler { PromptDelay = TimeSpan.FromSeconds(30) };
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
        // When set, the /prompt inject returns 502 with this body (a secret to be scrubbed).
        public string? PromptFailureBody { get; set; }
        // When set, the /prompt inject stalls this long (to trip the member deadline).
        public TimeSpan PromptDelay { get; set; } = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;

            if (path.EndsWith("/oauth/v2/token", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"access_token":"fake-token","token_type":"Bearer","expires_in":900}""");

            // brain-api front door for the repointed agy gemini member.
            if (uri.Host == Backend.Host)
            {
                if (request.Method == HttpMethod.Post && path == "/v1/sessions")
                    return Json(HttpStatusCode.OK, $$"""{"session_id":"sess-{{Guid.NewGuid():N}}"}""");
                if (request.Method == HttpMethod.Post && path.EndsWith("/prompt", StringComparison.Ordinal))
                {
                    if (PromptDelay > TimeSpan.Zero) await Task.Delay(PromptDelay, ct);
                    if (PromptFailureBody is not null)
                        return new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(PromptFailureBody) };
                    return Json(HttpStatusCode.Accepted, """{"state":"idle"}""");
                }
                if (request.Method == HttpMethod.Delete)
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"unrouted {path}") };
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
            new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
