using System.Net;
using System.Text;
using System.Text.Json;
using OpenWrtForum.Mcp;
using Xunit;

namespace OpenWrtForum.Mcp.Tests;

/// <summary>
/// The hardening-path tool coverage that the shape tests
/// (<see cref="ForumToolsTests"/>, <see cref="ForumWriteToolsTests"/>) do not
/// exercise:
///
/// <list type="bullet">
///   <item><b>Short-circuit</b> — a malformed parameter must be rejected with a
///   structured <c>{ ok:false, error }</c> envelope BEFORE any HTTP request is
///   made. Proven with a transport that <em>throws if it is ever reached</em>, so a
///   passing test is proof the tool never touched the network.</item>
///   <item><b>Error scrub end-to-end</b> — a fake transport emits a secret in an
///   error body; the surfaced error must contain <c>[redacted]</c>, not the raw
///   secret (the load-bearing leak test).</item>
///   <item><b>Timeout</b> — a transport that never completes is aborted by the
///   configured <c>HttpClient.Timeout</c>, not left to hang.</item>
/// </list>
/// </summary>
public sealed class ForumToolGuardTests
{
    /// <summary>A transport that throws the moment it is reached. If a tool's
    /// validation short-circuit works, this handler is never invoked and the test
    /// passes; if the tool wrongly issued an HTTP call, this makes it fail loudly.</summary>
    private sealed class ThrowIfReachedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new Xunit.Sdk.XunitException(
                $"HTTP was reached ({request.Method} {request.RequestUri}) — validation did NOT short-circuit");
    }

    /// <summary>A transport with a canned status + body, recording whether it was hit.</summary>
    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        public bool Hit { get; private set; }
        public CannedHandler(HttpStatusCode code, string body) { _code = code; _body = body; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Hit = true;
            return Task.FromResult(new HttpResponseMessage(_code)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>A transport that blocks until cancelled, so the client's timeout is
    /// what tears the call down.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK); // never reached
        }
    }

    private static DiscourseClient GuardClient(HttpMessageHandler h) =>
        new(new DiscourseOptions("https://forum.example.com", "", "", 30_000), h);

    private static DiscourseClient TimeoutClient(HttpMessageHandler h) =>
        // A 50 ms timeout so the hang test is fast + deterministic.
        new(new DiscourseOptions("https://forum.example.com", "", "", 50), h);

    private static (bool ok, string error) ParseErrorEnvelope(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var ok = doc.RootElement.GetProperty("ok").GetBoolean();
        var err = doc.RootElement.GetProperty("error").GetString()!;
        return (ok, err);
    }

    // ── short-circuit: malformed input never reaches HTTP ────────────────────

    [Fact]
    public async Task Search_EmptyQuery_ShortCircuitsBeforeHttp()
    {
        var json = await ForumTools.Search(GuardClient(new ThrowIfReachedHandler()), "  ", 1, CancellationToken.None);
        var (ok, err) = ParseErrorEnvelope(json);
        Assert.False(ok);
        Assert.Contains("query is required", err);
    }

    [Fact]
    public async Task Search_ControlCharQuery_ShortCircuitsBeforeHttp()
    {
        var json = await ForumTools.Search(GuardClient(new ThrowIfReachedHandler()), "bad\nq", 1, CancellationToken.None);
        var (ok, err) = ParseErrorEnvelope(json);
        Assert.False(ok);
        Assert.Contains("control characters", err);
    }

    [Fact]
    public async Task GetTopic_NonPositiveId_ShortCircuitsBeforeHttp()
    {
        var json = await ForumTools.GetTopic(GuardClient(new ThrowIfReachedHandler()), 0, 0, CancellationToken.None);
        var (ok, err) = ParseErrorEnvelope(json);
        Assert.False(ok);
        Assert.Contains("topic_id", err);
    }

    [Fact]
    public async Task GetLatest_LeadingDashSlug_ShortCircuitsBeforeHttp()
    {
        var json = await ForumTools.GetLatest(GuardClient(new ThrowIfReachedHandler()), "-flag", 0, CancellationToken.None);
        var (ok, err) = ParseErrorEnvelope(json);
        Assert.False(ok);
        Assert.Contains("must not start with '-'", err);
    }

    [Fact]
    public async Task GetLatest_PathSeparatorSlug_ShortCircuitsBeforeHttp()
    {
        var json = await ForumTools.GetLatest(GuardClient(new ThrowIfReachedHandler()), "../etc", 0, CancellationToken.None);
        var (ok, err) = ParseErrorEnvelope(json);
        Assert.False(ok);
        Assert.Contains("path separator", err);
    }

    [Fact]
    public async Task CreateTopic_EmptyTitle_ShortCircuitsBeforeAuthOrHttp()
    {
        // Credentialed client → a non-short-circuiting bug would drive the login
        // handshake (throwing in the handler). A pass proves no HTTP happened.
        var c = new DiscourseClient(
            new DiscourseOptions("https://forum.example.com", "alice", "s3cret", 30_000),
            new ThrowIfReachedHandler());
        var json = await ForumTools.CreateTopic(c, "  ", "a body", 8, null, CancellationToken.None);
        var (ok, err) = ParseErrorEnvelope(json);
        Assert.False(ok);
        Assert.Contains("title is required", err);
    }

    [Fact]
    public async Task CreateTopic_NulInBody_ShortCircuitsBeforeHttp()
    {
        var c = new DiscourseClient(
            new DiscourseOptions("https://forum.example.com", "alice", "s3cret", 30_000),
            new ThrowIfReachedHandler());
        // \0 is the C# NUL escape, NOT a literal NUL byte in the source.
        var json = await ForumTools.CreateTopic(c, "Title", "bad\0body", 8, null, CancellationToken.None);
        var (ok, err) = ParseErrorEnvelope(json);
        Assert.False(ok);
        Assert.Contains("control characters", err);
    }

    [Fact]
    public async Task CreateTopic_TooManyTags_ShortCircuitsBeforeHttp()
    {
        var c = new DiscourseClient(
            new DiscourseOptions("https://forum.example.com", "alice", "s3cret", 30_000),
            new ThrowIfReachedHandler());
        var many = Enumerable.Range(0, OpenWrtForumValidation.MaxTags + 1).Select(i => $"t{i}").ToArray();
        var json = await ForumTools.CreateTopic(c, "Title", "a body", 8, many, CancellationToken.None);
        var (ok, err) = ParseErrorEnvelope(json);
        Assert.False(ok);
        Assert.Contains("too many tags", err);
    }

    [Fact]
    public async Task CreatePost_NonPositiveTopicId_ShortCircuitsBeforeHttp()
    {
        var c = new DiscourseClient(
            new DiscourseOptions("https://forum.example.com", "alice", "s3cret", 30_000),
            new ThrowIfReachedHandler());
        var json = await ForumTools.CreatePost(c, topic_id: -1, body: "reply", ct: CancellationToken.None);
        var (ok, err) = ParseErrorEnvelope(json);
        Assert.False(ok);
        Assert.Contains("topic_id", err);
    }

    [Fact]
    public async Task GetNotifications_UnknownFilter_ShortCircuitsBeforeHttp()
    {
        var c = new DiscourseClient(
            new DiscourseOptions("https://forum.example.com", "alice", "s3cret", 30_000),
            new ThrowIfReachedHandler());
        var json = await ForumTools.GetNotifications(c, "everything", CancellationToken.None);
        var (ok, err) = ParseErrorEnvelope(json);
        Assert.False(ok);
        Assert.Contains("filter must be", err);
    }

    [Fact]
    public async Task ValidInput_DoesReachHttp_ProvingTheGuardIsNotAlwaysOn()
    {
        // A control test: valid input must NOT short-circuit — the transport is hit.
        var canned = new CannedHandler(HttpStatusCode.OK, """{ "topics": [], "posts": [] }""");
        var json = await ForumTools.Search(GuardClient(canned), "valid query", 1, CancellationToken.None);
        Assert.True(canned.Hit);
        // Success path returns the topics/posts envelope (no ok:false error).
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("topics", out _));
    }

    // ── error scrub end-to-end: a secret in an upstream body is redacted ─────

    [Fact]
    public async Task UpstreamErrorBody_WithSecret_IsRedacted_NotLeaked()
    {
        // The upstream forum returns a verbose 500 whose body echoes a credential.
        // The tool surfaces a structured envelope; the secret must be scrubbed.
        var leaky = new CannedHandler(HttpStatusCode.InternalServerError,
            """{ "error": "login failed", "detail": "password=hunter2-supersecret" }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ForumTools.ListCategories(GuardClient(leaky), CancellationToken.None));

        Assert.True(leaky.Hit);
        // The diagnostic status survives…
        Assert.Contains("discourse 500", ex.Message);
        // …but the secret value is redacted by OpenWrtForumErrors.Sanitize.
        Assert.DoesNotContain("hunter2-supersecret", ex.Message);
        Assert.Contains("[redacted]", ex.Message);
    }

    [Fact]
    public async Task UpstreamErrorBody_WithPrivateKeyBlock_IsRedacted()
    {
        var pem =
            "-----BEGIN RSA PRIVATE KEY-----\\nMIIEowIBAAKCAQEA\\n-----END RSA PRIVATE KEY-----";
        var leaky = new CannedHandler(HttpStatusCode.BadGateway,
            $$"""{ "error": "proxy error", "leaked": "{{pem}}" }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ForumTools.ListCategories(GuardClient(leaky), CancellationToken.None));

        Assert.DoesNotContain("MIIEowIBAAKCAQEA", ex.Message);
        Assert.Contains("[redacted]", ex.Message);
    }

    // ── timeout: a hung upstream is torn down by HttpClient.Timeout ──────────

    [Fact]
    public async Task HangingUpstream_IsAbortedByConfiguredTimeout()
    {
        var client = TimeoutClient(new HangingHandler());

        // HttpClient surfaces a timeout as TaskCanceledException (a subclass of
        // OperationCanceledException). Assert the call does not hang forever.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ForumTools.ListCategories(client, CancellationToken.None));
    }
}
