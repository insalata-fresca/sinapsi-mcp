using System.Net;
using System.Text;
using System.Text.Json;
using Sinapsi.Forge;
using Sinapsi.Forge.Gitea;
using Sinapsi.Forge.Tools;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// End-to-end hardening proofs driven through the REAL tool methods over the Gitea adapter
/// with a fake HTTP transport:
///   • the validation guard SHORT-CIRCUITS before any HTTP call (the transport throws if reached);
///   • the LOAD-BEARING leg — a transport emits a secret in an error body and the tool returns
///     the scrubbed [redacted] envelope, never the raw secret;
///   • a timeout (the transport delays past the client timeout) surfaces as a scrubbed error, not a
///     raw unhandled exception.
/// </summary>
public sealed class ForgeToolHardeningTests
{
    /// <summary>A transport that FAILS the test if it is ever reached — proves a tool short-circuited.</summary>
    private sealed class ThrowIfReachedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new Xunit.Sdk.XunitException($"HTTP must NOT be reached — validation should short-circuit; got {request.Method} {request.RequestUri}");
    }

    /// <summary>Returns a fixed status + body for every request (used to plant a secret in an error body).</summary>
    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Delays longer than the client timeout, then would respond — proves the timeout fires.</summary>
    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(delay, ct);   // cancelled by HttpClient.Timeout
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private static IForgeClient Client(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://forge.example/api/v1/") };
        if (timeout is { } t) http.Timeout = t;
        return new GiteaForgeClient(http);
    }

    private static JsonElement Env(object result) => JsonSerializer.SerializeToElement(result);

    // ── validation short-circuits BEFORE any HTTP call ──────────────────────────

    [Fact]
    public async Task GetRepo_bad_owner_short_circuits_without_touching_http()
    {
        var forge = Client(new ThrowIfReachedHandler());
        var result = Env(await RepoTools.GetRepo(forge, "-evil", "repo"));
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("owner", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetFile_path_traversal_short_circuits_without_touching_http()
    {
        var forge = Client(new ThrowIfReachedHandler());
        var result = Env(await ContentTools.GetFile(forge, "o", "r", "../../etc/passwd"));
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("traversal", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetIssue_non_positive_number_short_circuits_without_touching_http()
    {
        var forge = Client(new ThrowIfReachedHandler());
        var result = Env(await IssueTools.GetIssue(forge, "o", "r", 0));
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("number", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SearchRepos_empty_query_short_circuits_without_touching_http()
    {
        var forge = Client(new ThrowIfReachedHandler());
        var result = Env(await RepoTools.SearchRepos(forge, "   "));
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("query", result.GetProperty("error").GetString());
    }

    // ── LOAD-BEARING: a secret in an upstream error body must be scrubbed ────────

    [Fact]
    public async Task GetRepo_upstream_error_body_with_a_secret_is_redacted_not_leaked()
    {
        // The forge returns a 500 whose body leaks a token — the tool must surface [redacted].
        const string secret = "ghp_SUPERSECRETTOKEN12345";
        var handler = new FixedResponseHandler(HttpStatusCode.InternalServerError, $"{{\"message\":\"boom token={secret}\"}}");
        var forge = Client(handler);

        var result = Env(await RepoTools.GetRepo(forge, "o", "r"));

        Assert.Equal(1, handler.Calls);                        // HTTP WAS reached (valid params)
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(500, result.GetProperty("status").GetInt32());   // RAW status verdict preserved
        var error = result.GetProperty("error").GetString()!;
        Assert.Contains("[redacted]", error);
        Assert.DoesNotContain(secret, error);                  // the raw secret never reaches the caller
    }

    [Fact]
    public async Task DeleteRepo_upstream_error_with_a_pem_key_is_redacted()
    {
        const string body = "500: -----BEGIN EC PRIVATE KEY-----\nLEAKEDKEYMATERIAL\n-----END EC PRIVATE KEY-----";
        var forge = Client(new FixedResponseHandler(HttpStatusCode.InternalServerError, body));
        var result = Env(await RepoTools.DeleteRepo(forge, "o", "r"));
        var error = result.GetProperty("error").GetString()!;
        Assert.Contains("[redacted]", error);
        Assert.DoesNotContain("LEAKEDKEYMATERIAL", error);
    }

    // ── timeout path fires (surfaced as a scrubbed error, not an unhandled throw) ─

    [Fact]
    public async Task GetRepo_client_timeout_is_surfaced_as_a_structured_error()
    {
        // Client timeout well below the handler delay → the request is cancelled by the timeout.
        var forge = Client(new SlowHandler(TimeSpan.FromSeconds(30)), timeout: TimeSpan.FromMilliseconds(50));
        var result = Env(await RepoTools.GetRepo(forge, "o", "r"));
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("error").GetString()));
    }
}
