using System.Net;
using System.Text;
using Xunit;
using Zitadel.Mcp;
using Zitadel.Mcp.Api;
using Zitadel.Mcp.Tools;

namespace Zitadel.Mcp.Tests;

/// <summary>
/// Tool-level coverage for the two hardening paths every tool docstring promises:
///
/// <list type="bullet">
///   <item><b>invalid input → structured error, no HTTP call.</b> Each tool is driven with a
///   malformed parameter through a <see cref="ThrowingHandler"/> that FAILS the test if it is
///   ever reached — proving validation short-circuits before any upstream request, exactly as
///   the StepCa guard tests point <c>StepBin</c> at a nonexistent path.</item>
///   <item><b>upstream failure → sanitized error, end-to-end.</b> A scripted handler returns a
///   non-2xx body carrying a fake secret; the tool must surface <c>[redacted]</c>, not the raw
///   secret — the load-bearing "the scrub really fires at the tool level" leg (mirrors StepCa's
///   <c>SubprocessToolErrorTests</c>).</item>
/// </list>
/// </summary>
public sealed class ZitadelToolGuardTests
{
    // A handler that must never be reached: any request is a test failure. Proves a validation
    // guard short-circuited before the HTTP layer.
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

    private static ZitadelClient GuardClient(string agentKeyDir = "/tmp/agent-keys-test")
        => new(new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://auth.example.com/") }, Config(agentKeyDir));

    private static ZitadelClient ScriptedClient(HttpStatusCode status, string body, string agentKeyDir = "/tmp/agent-keys-test")
        => new(new HttpClient(new ScriptedHandler(status, body)) { BaseAddress = new Uri("https://auth.example.com/") }, Config(agentKeyDir));

    private static ZitadelConfig Config(string agentKeyDir) =>
        new(
            BaseUrl: "https://auth.example.com",
            AuthMode: ZitadelAuthMode.Pat,
            Token: "svc-token",
            SaKeyFile: null,
            Issuer: "https://auth.example.com",
            HostHeader: "auth.example.com",
            Port: ZitadelConfig.DefaultPort,
            AgentKeyDir: agentKeyDir,
            HttpTimeoutMs: ZitadelConfig.DefaultHttpTimeoutMs);

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
    public async Task GetUser_EmptyId_RejectedBeforeHttp(string id)
    {
        var (ok, error) = Envelope(await UserTools.GetUser(GuardClient(), id));
        Assert.False(ok);
        Assert.Equal("userId is required", error);
    }

    [Fact]
    public async Task GetUser_PathSeparatorId_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await UserTools.GetUser(GuardClient(), "a/b"));
        Assert.False(ok);
        Assert.Contains("path separator", error!);
    }

    [Fact]
    public async Task ListUsers_OutOfRangeLimit_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await UserTools.ListUsers(GuardClient(), limit: 0));
        Assert.False(ok);
        Assert.Contains("out of range", error!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateProject_EmptyName_RejectedBeforeHttp(string name)
    {
        var (ok, error) = Envelope(await ProjectTools.CreateProject(GuardClient(), name));
        Assert.False(ok);
        Assert.Equal("name is required", error);
    }

    [Fact]
    public async Task CreateOidcApp_EmptyRedirectUris_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await OidcAppTools.CreateOidcApp(
            GuardClient(), "proj1", "my-app", redirectUris: Array.Empty<string>()));
        Assert.False(ok);
        Assert.Contains("redirectUris is required", error!);
    }

    [Fact]
    public async Task CreateOidcApp_ControlCharInName_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await OidcAppTools.CreateOidcApp(
            GuardClient(), "proj1", "bad\nname", redirectUris: new[] { "https://ok" }));
        Assert.False(ok);
        Assert.Contains("control characters", error!);
    }

    [Fact]
    public async Task DeleteOidcApp_EmptyAppId_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await OidcAppTools.DeleteOidcApp(GuardClient(), "proj1", ""));
        Assert.False(ok);
        Assert.Equal("appId is required", error);
    }

    [Fact]
    public async Task RegenerateOidcSecret_EmptyProjectId_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await OidcAppTools.RegenerateOidcSecret(GuardClient(), "", "app1"));
        Assert.False(ok);
        Assert.Equal("projectId is required", error);
    }

    [Fact]
    public async Task CreateMachineUser_EmptyUsername_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await MachineUserTools.CreateMachineUser(GuardClient(), username: ""));
        Assert.False(ok);
        Assert.Equal("username is required", error);
    }

    [Fact]
    public async Task DeleteMachineUser_EmptyId_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await MachineUserTools.DeleteMachineUser(GuardClient(), ""));
        Assert.False(ok);
        Assert.Equal("userId is required", error);
    }

    [Fact]
    public async Task CreatePat_BadExpiration_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await MachineUserTools.CreatePat(GuardClient(), "user1", expirationIso: "not-a-date"));
        Assert.False(ok);
        Assert.Contains("not a valid", error!);
    }

    [Fact]
    public async Task CreateMachineKey_PathTraversalAgentFile_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await MachineUserTools.CreateMachineKey(
            GuardClient(), userId: "user1", agentFile: "../etc/passwd"));
        Assert.False(ok);
        Assert.Contains("bare basename", error!);
    }

    [Fact]
    public async Task CreateMachineKey_EmptyUserId_RejectedBeforeHttp()
    {
        var (ok, error) = Envelope(await MachineUserTools.CreateMachineKey(
            GuardClient(), userId: "", agentFile: "agent-x"));
        Assert.False(ok);
        Assert.Equal("userId is required", error);
    }

    // ── upstream failure → SANITIZED error, end-to-end at the tool level ────────

    /// <summary>
    /// The upstream returns a non-2xx body that (pathologically) echoes a credential. The tool
    /// must surface the { ok:false, status, error } envelope with the secret redacted — proving
    /// the ZitadelErrors scrub fires end-to-end, not just in its isolated unit test.
    /// </summary>
    [Fact]
    public async Task GetUser_UpstreamErrorWithSecret_IsRedacted()
    {
        var client = ScriptedClient(HttpStatusCode.InternalServerError,
            """{"message":"upstream failure, token=leaked-bearer-abc.def.ghi while proxying"}""");

        var (ok, error) = Envelope(await UserTools.GetUser(client, "user1"));

        Assert.False(ok);
        // The diagnostic survives…
        Assert.Contains("upstream failure", error!);
        // …but the secret value is scrubbed by ZitadelErrors.Sanitize.
        Assert.DoesNotContain("leaked-bearer-abc.def.ghi", error);
        Assert.Contains("[redacted]", error);
    }

    /// <summary>
    /// A mutating tool takes the same end-to-end sanitized-error path: a client_secret echoed in
    /// the upstream failure body must be redacted out of the tool's error envelope.
    /// </summary>
    [Fact]
    public async Task RegenerateOidcSecret_UpstreamErrorWithSecret_IsRedacted()
    {
        var client = ScriptedClient(HttpStatusCode.BadGateway,
            """{"message":"rotation failed: client_secret=old-secret-value-xyz still active"}""");

        var (ok, error) = Envelope(await OidcAppTools.RegenerateOidcSecret(client, "proj1", "app1"));

        Assert.False(ok);
        Assert.Contains("rotation failed", error!);
        Assert.DoesNotContain("old-secret-value-xyz", error);
        Assert.Contains("[redacted]", error);
    }

    /// <summary>
    /// The status leg: a non-2xx upstream surfaces the real HTTP status on the envelope
    /// (mirrors the ZitadelApiException status carry-through).
    /// </summary>
    [Fact]
    public async Task ListProjects_UpstreamForbidden_SurfacesStatus()
    {
        var client = ScriptedClient(HttpStatusCode.Forbidden, """{"message":"insufficient scope"}""");

        var result = await ProjectTools.ListProjects(client, limit: 100);

        var ok = (bool)result.GetType().GetProperty("ok")!.GetValue(result)!;
        var status = (int?)result.GetType().GetProperty("status")!.GetValue(result);
        Assert.False(ok);
        Assert.Equal(403, status);
    }

    /// <summary>
    /// The create_machine_key upstream-error path is guarded by its own try/catch (not
    /// ZitadelToolGuard). Its error string must also be sanitized end-to-end.
    /// </summary>
    [Fact]
    public async Task CreateMachineKey_UpstreamErrorWithSecret_IsRedacted()
    {
        var dir = Directory.CreateTempSubdirectory("zitadel-key-test-").FullName;
        try
        {
            var client = ScriptedClient(HttpStatusCode.InternalServerError,
                """{"message":"key issuance failed, api_key=leaked-key-value in log"}""", agentKeyDir: dir);

            var result = await MachineUserTools.CreateMachineKey(client, userId: "user1", agentFile: "agent-x");
            var (ok, error) = Envelope(result);

            Assert.False(ok);
            Assert.DoesNotContain("leaked-key-value", error!);
            Assert.Contains("[redacted]", error);
            // The key file must NOT have been written on the failure path.
            Assert.False(File.Exists(Path.Combine(dir, "agent-x.json")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
