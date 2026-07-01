using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infisical.Mcp.Tests;

/// <summary>
/// Tool-level coverage for the two hardening paths the happy-path tool tests never reach:
/// (1) invalid input → structured <c>{ok:false,error}</c> BEFORE any REST call, and
/// (2) an upstream/REST failure → a SANITIZED structured error end-to-end at the tool
/// level. This mirrors StepCa's <c>SubprocessToolErrorTests</c>: the guard leg points the
/// backend at a nonexistent host so validation/short-circuit fires without a live server,
/// and the failure leg drives a fake HTTP backend that emits a secret so we can assert the
/// tool returns <c>[redacted]</c> — not the raw secret — and that the timeout path fires.
/// </summary>
public sealed class ToolUpstreamErrorTests
{
    private static InfisicalOptions Opt(int timeoutMs = 30_000) => new()
    {
        HostUrl = "https://secrets.example.org",
        ClientId = "cid",
        ClientSecret = "csecret",
        ProjectId = "proj",
        EnvName = "dev",
        HttpTimeoutMs = timeoutMs,
    };

    private static InfisicalTools Build(HttpMessageHandler handler, int timeoutMs = 30_000)
    {
        var opt = Opt(timeoutMs);
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(opt.HttpTimeoutMs) };
        var client = new InfisicalClient(http, opt, NullLogger<InfisicalClient>.Instance);
        return new InfisicalTools(client, opt);
    }

    private static JsonElement Parse(string s) => JsonDocument.Parse(s).RootElement;

    // ── (1) invalid input → structured error, no REST call ──────────────────────────
    // The handler THROWS if it is ever reached, proving validation short-circuits before
    // any HTTP request (the analogue of StepCa's /nonexistent/step guard binaries).
    private sealed class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("HTTP was reached, but validation should have short-circuited");
    }

    [Theory]
    [InlineData("", "api")]
    [InlineData("web", "")]
    [InlineData("-flag", "api")]
    [InlineData("web", "a/b")]
    public async Task issue_nats_nkey_invalid_input_returns_structured_error_without_calling_the_api(
        string group, string service)
    {
        var tools = Build(new ExplodingHandler());

        var r = Parse(await tools.issue_nats_nkey(group, service, CancellationToken.None));

        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task issue_random_secret_absurd_byte_count_is_rejected_without_calling_the_api()
    {
        var tools = Build(new ExplodingHandler());

        var r = Parse(await tools.issue_random_secret(
            "web", "api", "K", bytes: InfisicalValidation.MaxRandomBytes + 1, CancellationToken.None));

        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Contains("out of range", r.GetProperty("error").GetString());
    }

    [Fact]
    public async Task set_secret_empty_value_is_rejected_without_calling_the_api()
    {
        var tools = Build(new ExplodingHandler());

        var r = Parse(await tools.set_secret("web", "api", "K", value: "", CancellationToken.None));

        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Equal("value is required", r.GetProperty("error").GetString());
    }

    [Fact]
    public async Task list_secrets_invalid_input_is_rejected_without_calling_the_api()
    {
        var tools = Build(new ExplodingHandler());

        var r = Parse(await tools.list_secrets("bad\nname", "api", CancellationToken.None));

        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.Contains("control characters", r.GetProperty("error").GetString());
    }

    // ── (2) upstream failure → SANITIZED structured error, end-to-end ────────────────
    // A handler that logs in fine, then makes the secret-upsert fail with an exception
    // whose message carries a fake secret. The tool's catch → InfisicalErrors.Sanitize
    // must redact the secret before it reaches the caller.
    private sealed class SecretLeakingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/v1/auth/universal-auth/login"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"accessToken":"test-token"}""", Encoding.UTF8, "application/json"),
                };

            // Any non-login request throws with a secret embedded in the message. This is
            // the realistic "upstream error text carries a credential" case the scrub
            // contract exists for.
            await Task.Yield();
            throw new InvalidOperationException(
                "infisical upsert failed: password=hunter2-supersecret in response");
        }
    }

    [Fact]
    public async Task issue_random_secret_upstream_error_is_sanitized_end_to_end()
    {
        var tools = Build(new SecretLeakingHandler());

        var r = Parse(await tools.issue_random_secret("web", "api", "DB_PASSWORD", bytes: 16, CancellationToken.None));

        Assert.False(r.GetProperty("ok").GetBoolean());
        var err = r.GetProperty("error").GetString()!;
        // The diagnostic survives…
        Assert.Contains("infisical upsert failed", err);
        // …but the secret value is redacted.
        Assert.DoesNotContain("hunter2-supersecret", err);
        Assert.Contains("[redacted]", err);
    }

    [Fact]
    public async Task set_secret_upstream_error_is_sanitized_end_to_end()
    {
        var tools = Build(new SecretLeakingHandler());

        var r = Parse(await tools.set_secret("web", "api", "VENDOR_TOKEN", "tok-123", CancellationToken.None));

        Assert.False(r.GetProperty("ok").GetBoolean());
        var err = r.GetProperty("error").GetString()!;
        Assert.DoesNotContain("hunter2-supersecret", err);
        Assert.Contains("[redacted]", err);
    }

    [Fact]
    public async Task issue_nats_nkey_upstream_error_is_sanitized_and_never_leaks_the_seed()
    {
        var tools = Build(new SecretLeakingHandler());

        var resultJson = await tools.issue_nats_nkey("web", "api", CancellationToken.None);
        var r = Parse(resultJson);

        Assert.False(r.GetProperty("ok").GetBoolean());
        var err = r.GetProperty("error").GetString()!;
        Assert.DoesNotContain("hunter2-supersecret", err);
        Assert.Contains("[redacted]", err);
        // The generated seed (S-prefixed base32) must never appear anywhere in the error
        // envelope — the whole point of the server.
        Assert.DoesNotContain("SEED", err); // no key material label leaks either
        Assert.False(r.TryGetProperty("seed", out _));
    }

    [Fact]
    public async Task list_secrets_upstream_error_is_sanitized_end_to_end()
    {
        var tools = Build(new SecretLeakingHandler());

        var r = Parse(await tools.list_secrets("web", "api", CancellationToken.None));

        Assert.False(r.GetProperty("ok").GetBoolean());
        var err = r.GetProperty("error").GetString()!;
        Assert.DoesNotContain("hunter2-supersecret", err);
        Assert.Contains("[redacted]", err);
    }

    // ── the timeout path actually fires ─────────────────────────────────────────────
    // A handler that stalls past the client's (tiny) configured timeout. The HttpClient
    // cancels the request; the tool catches it and returns a structured error rather than
    // letting the exception escape. This proves the bounded-call hardening is real.
    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Honour the cancellation token so the HttpClient timeout can fire promptly.
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task set_secret_returns_structured_error_when_the_call_times_out()
    {
        // 50 ms client timeout against a handler that stalls 30 s → the timeout fires.
        var tools = Build(new StallingHandler(), timeoutMs: 50);

        var r = Parse(await tools.set_secret("web", "api", "K", "v", CancellationToken.None));

        Assert.False(r.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("error").GetString()));
    }
}
