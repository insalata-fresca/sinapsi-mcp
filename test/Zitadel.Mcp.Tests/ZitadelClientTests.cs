using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using Zitadel.Mcp.Api;

namespace Zitadel.Mcp.Tests;

/// <summary>
/// Verifies the ZITADEL adapter shapes the management-API request (path + method + search
/// body) and surfaces a non-2xx upstream response as a <see cref="ZitadelApiException"/>
/// carrying the real status + body. Uses a scripted fake transport — no live instance.
/// </summary>
public sealed class ZitadelClientTests
{
    private sealed class ScriptedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string? Body)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var reqBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, reqBody));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly Zitadel.Mcp.ZitadelConfig TestConfig = new(
        BaseUrl: "https://auth.example.com",
        AuthMode: Zitadel.Mcp.ZitadelAuthMode.Pat,
        Token: "svc-token",
        SaKeyFile: null,
        Issuer: "https://auth.example.com",
        HostHeader: "auth.example.com",
        Port: Zitadel.Mcp.ZitadelConfig.DefaultPort,
        AgentKeyDir: Zitadel.Mcp.ZitadelConfig.DefaultAgentKeyDir,
        HttpTimeoutMs: Zitadel.Mcp.ZitadelConfig.DefaultHttpTimeoutMs);

    private static (ZitadelClient client, ScriptedHandler handler) Build(HttpStatusCode status, string body)
    {
        var handler = new ScriptedHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://auth.example.com/") };
        return (new ZitadelClient(http, TestConfig), handler);
    }

    [Fact]
    public async Task ListUsers_posts_a_search_with_the_limit_to_the_management_path()
    {
        var (client, handler) = Build(HttpStatusCode.OK, """{"result":[]}""");

        var res = await client.ListUsersAsync(25, CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal("/management/v1/users/_search", call.Path);
        using var sent = JsonDocument.Parse(call.Body!);
        Assert.Equal(25, sent.RootElement.GetProperty("query").GetProperty("limit").GetInt32());
        Assert.True(res.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task GetUser_gets_the_user_by_id_path()
    {
        var (client, handler) = Build(HttpStatusCode.OK, """{"user":{"id":"42"}}""");

        await client.GetUserAsync("42", CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal("/management/v1/users/42", call.Path);
    }

    [Fact]
    public async Task ListApps_posts_the_project_apps_search_path()
    {
        var (client, handler) = Build(HttpStatusCode.OK, """{"result":[]}""");

        await client.ListAppsAsync("proj1", 10, CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("/management/v1/projects/proj1/apps/_search", call.Path);
    }

    [Fact]
    public async Task Non_2xx_surfaces_status_and_body_as_ZitadelApiException()
    {
        var (client, _) = Build(HttpStatusCode.Forbidden, """{"message":"insufficient scope"}""");

        var ex = await Assert.ThrowsAsync<ZitadelApiException>(
            () => client.ListProjectsAsync(100, CancellationToken.None));

        Assert.Equal(403, ex.Status);
        Assert.Contains("insufficient scope", ex.Message);
    }
}
