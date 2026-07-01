using System.Net;
using System.Text;
using Metabase.Mcp.Api;
using Metabase.Mcp.Tools;
using Xunit;

namespace Metabase.Mcp.Tests;

// -----------------------------------------------------------------------------
// The load-bearing hardening legs, end-to-end at the TOOL level:
//   (1) invalid input -> structured error, and NO HTTP request is issued (a fake
//       transport FAILS the test if reached, proving the validation short-circuit);
//   (2) an upstream failure whose body echoes a secret -> the tool returns
//       [redacted], not the raw secret (proving MetabaseErrors fires in the guard).
// Mirrors StepCa's SubprocessToolErrorTests / Zitadel's ZitadelToolGuardTests.
// -----------------------------------------------------------------------------

/// <summary>
/// Tool-level coverage for the two hardening paths every mutating/query tool promises:
/// invalid input is rejected BEFORE any HTTP call, and an upstream error is surfaced
/// through the sanitizer so a leaked credential is redacted.
/// </summary>
public sealed class MetabaseToolGuardTests
{
    // A handler that must never be reached: any request is a test failure. Proves a validation
    // guard short-circuited before the HTTP layer (the analogue of pointing StepBin at a
    // nonexistent path).
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new Xunit.Sdk.XunitException(
                $"an HTTP request was issued ({request.Method} {request.RequestUri}) — validation should have short-circuited");
    }

    // A handler that returns a scripted status + body for every request.
    private sealed class ScriptedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private static MetabaseClient GuardClient()
        => new(new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://metrics.example.com/") });

    private static MetabaseClient ScriptedClient(HttpStatusCode status, string body)
        => new(new HttpClient(new ScriptedHandler(status, body)) { BaseAddress = new Uri("https://metrics.example.com/") });

    private static (bool ok, string? error) Envelope(object result)
    {
        var ok = (bool)result.GetType().GetProperty("ok")!.GetValue(result)!;
        var error = (string?)result.GetType().GetProperty("error")?.GetValue(result);
        return (ok, error);
    }

    // ── invalid input → structured error, NO HTTP call ─────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunNativeQuery_EmptySql_RejectedBeforeHttp(string sql)
    {
        var (ok, error) = Envelope(await CardTools.RunNativeQuery(GuardClient(), databaseId: 1, sql: sql));
        Assert.False(ok);
        Assert.Equal("sql is required", error);
    }

    [Fact]
    public async Task RunNativeQuery_NulInSql_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await CardTools.RunNativeQuery(GuardClient(), databaseId: 1, sql: "SELECT\0 1"));
        Assert.False(ok);
        Assert.Contains("control characters", error!);
    }

    [Fact]
    public async Task RunCardQuery_MalformedParametersJson_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await CardTools.RunCardQuery(GuardClient(), cardId: 7, parametersJson: "{not json"));
        Assert.False(ok);
        Assert.Contains("not valid JSON", error!);
    }

    [Fact]
    public async Task Request_DisallowedMethod_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await ApiTools.Request(GuardClient(), method: "PATCH", path: "/api/card/1"));
        Assert.False(ok);
        Assert.Contains("not allowed", error!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Request_EmptyMethod_RejectedBeforeHttp(string method)
    {
        var (ok, error) = Envelope(await ApiTools.Request(GuardClient(), method: method, path: "/api/card/1"));
        Assert.False(ok);
        Assert.Contains("method is required", error!);
    }

    [Fact]
    public async Task Request_AbsoluteUrlPath_RejectedBeforeHttp()
    {
        // A caller must not be able to point the server-held API key at an arbitrary host.
        var (ok, error) = Envelope(await ApiTools.Request(GuardClient(), method: "GET", path: "https://evil.example/api/card/1"));
        Assert.False(ok);
        Assert.Contains("absolute URL", error!);
    }

    [Fact]
    public async Task Request_NonApiPath_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await ApiTools.Request(GuardClient(), method: "GET", path: "/etc/passwd"));
        Assert.False(ok);
        Assert.Contains("Metabase API", error!);
    }

    [Fact]
    public async Task Request_MalformedBodyJson_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await ApiTools.Request(GuardClient(), method: "POST", path: "/api/card", bodyJson: "}{"));
        Assert.False(ok);
        Assert.Contains("not valid JSON", error!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Search_EmptyQuery_RejectedBeforeHttp(string q)
    {
        var (ok, error) = Envelope(await ApiTools.Search(GuardClient(), q: q));
        Assert.False(ok);
        Assert.Equal("q is required", error);
    }

    [Fact]
    public async Task GetCollectionItems_EmptyId_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await CollectionTools.GetCollectionItems(GuardClient(), id: ""));
        Assert.False(ok);
        Assert.Equal("id is required", error);
    }

    [Fact]
    public async Task CreateDatabase_MalformedDetailsJson_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await DatabaseTools.CreateDatabase(
            GuardClient(), name: "fin", engine: "postgres", detailsJson: "{not json"));
        Assert.False(ok);
        Assert.Contains("not valid JSON", error!);
    }

    [Fact]
    public async Task CreateDatabase_EmptyEngine_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await DatabaseTools.CreateDatabase(
            GuardClient(), name: "fin", engine: "", detailsJson: """{"host":"h"}"""));
        Assert.False(ok);
        Assert.Contains("engine is required", error!);
    }

    [Fact]
    public async Task UpdateCard_MalformedPatchJson_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await CardTools.UpdateCard(GuardClient(), id: 3, patchJson: "not json"));
        Assert.False(ok);
        Assert.Contains("not valid JSON", error!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateDashboard_EmptyName_RejectedBeforeHttp(string name)
    {
        var (ok, error) = Envelope(await DashboardTools.CreateDashboard(GuardClient(), name: name));
        Assert.False(ok);
        Assert.Equal("name is required", error);
    }

    [Fact]
    public async Task CreateNativeCard_EmptySql_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await CardTools.CreateNativeCard(
            GuardClient(), name: "q", databaseId: 1, sql: ""));
        Assert.False(ok);
        Assert.Equal("sql is required", error);
    }

    // ── upstream failure → SANITIZED error, end-to-end at the tool level ────────

    /// <summary>
    /// The load-bearing leg (mirrors StepCa's redaction test): a failing upstream whose body
    /// echoes a credential must surface { ok:false, status, error } with the secret redacted —
    /// proving MetabaseErrors.Sanitize really fires inside the guard, not just in its unit test.
    /// A read tool takes the same path (reads are guarded too).
    /// </summary>
    [Fact]
    public async Task ListDatabases_UpstreamErrorWithSecret_IsRedacted()
    {
        var client = ScriptedClient(HttpStatusCode.InternalServerError,
            """{"message":"upstream failure, password=leaked-db-pw-abc while connecting"}""");

        var (ok, error) = Envelope(await DatabaseTools.ListDatabases(client));

        Assert.False(ok);
        Assert.Contains("upstream failure", error!);          // diagnostic survives
        Assert.DoesNotContain("leaked-db-pw-abc", error);     // secret scrubbed
        Assert.Contains("[redacted]", error);
    }

    /// <summary>
    /// A mutating tool takes the same end-to-end sanitized-error path: a DB password echoed in
    /// the upstream failure body (as create_database would carry) is redacted out of the envelope.
    /// </summary>
    [Fact]
    public async Task CreateDatabase_UpstreamErrorWithSecret_IsRedacted()
    {
        var client = ScriptedClient(HttpStatusCode.BadGateway,
            """{"message":"connection rejected: password=super-secret-pw for user metabase_ro"}""");

        var (ok, error) = Envelope(await DatabaseTools.CreateDatabase(
            client, name: "fin", engine: "postgres", detailsJson: """{"host":"h","dbname":"d"}"""));

        Assert.False(ok);
        Assert.Contains("connection rejected", error!);
        Assert.DoesNotContain("super-secret-pw", error);
        Assert.Contains("[redacted]", error);
    }

    /// <summary>
    /// The status leg: a non-2xx upstream surfaces the real HTTP status on the envelope
    /// (mirrors the MetabaseApiException status carry-through).
    /// </summary>
    [Fact]
    public async Task GetCard_UpstreamNotFound_SurfacesStatus()
    {
        var client = ScriptedClient(HttpStatusCode.NotFound, """{"message":"no such card"}""");

        var result = await CardTools.GetCard(client, cardId: 999);

        var ok = (bool)result.GetType().GetProperty("ok")!.GetValue(result)!;
        var status = (int?)result.GetType().GetProperty("status")!.GetValue(result);
        Assert.False(ok);
        Assert.Equal(404, status);
    }

    /// <summary>
    /// The generic <c>request</c> escape hatch, on a valid method + path, also routes its
    /// upstream error through the sanitizer.
    /// </summary>
    [Fact]
    public async Task Request_UpstreamErrorWithSecret_IsRedacted()
    {
        var client = ScriptedClient(HttpStatusCode.Unauthorized,
            """{"message":"denied, x-api-key=leaked-mb-key rejected"}""");

        var (ok, error) = Envelope(await ApiTools.Request(client, method: "GET", path: "/api/permissions"));

        Assert.False(ok);
        Assert.DoesNotContain("leaked-mb-key", error!);
        Assert.Contains("[redacted]", error);
    }
}
