using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using Sinapsi.Mcp;
using Xunit;

namespace Sinapsi.Mcp.Tests;

public class SinapsiMcpServerBuilderTests
{
    [Fact]
    public void AddSinapsiMcpServer_registers_gateway_client_with_infinite_timeout()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddSinapsiMcpServer("example-mcp", "1.2.3").WithHttpTransport();

        using var app = builder.Build();

        // The typed HttpClient<GatewayMcpClient> is resolvable and its timeout was
        // overridden away from the default 100s to InfiniteTimeSpan.
        var factory = app.Services.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(GatewayMcpClient));
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);

        var client = app.Services.GetRequiredService<GatewayMcpClient>();
        Assert.NotNull(client);
    }

    [Fact]
    public void AddSinapsiMcpServer_returns_chainable_mcp_builder()
    {
        var builder = WebApplication.CreateBuilder();

        var mcpBuilder = builder.AddSinapsiMcpServer("example-mcp", "0.1.0");

        // Returns the SDK builder so consumers keep chaining transport + tools.
        Assert.NotNull(mcpBuilder);
        var chained = mcpBuilder.WithHttpTransport();
        Assert.Same(mcpBuilder, chained);
    }

    [Fact]
    public async Task MapSinapsiMcp_strips_stray_session_id_when_stateless()
    {
        using var app = BuildProbeApp(stateless: true);
        await app.StartAsync();

        var client = app.GetTestServer().CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/probe");
        req.Headers.TryAddWithoutValidation("Mcp-Session-Id", "stray-from-proxy");

        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        // The stateless strip middleware removed the header before it reached /probe.
        Assert.Equal("session-id-absent", body);

        await app.StopAsync();
    }

    [Fact]
    public async Task MapSinapsiMcp_keeps_session_id_when_stateful()
    {
        using var app = BuildProbeApp(stateless: false);
        await app.StartAsync();

        var client = app.GetTestServer().CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/probe");
        req.Headers.TryAddWithoutValidation("Mcp-Session-Id", "real-session");

        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        // Stateful servers thread a real session id; the header is left intact.
        Assert.Equal("session-id-present", body);

        await app.StopAsync();
    }

    [Fact]
    public void MapSinapsiMcp_binds_listen_address_from_env_prefix()
    {
        Environment.SetEnvironmentVariable("UNIT_PROBE_HOST", "127.0.0.1");
        Environment.SetEnvironmentVariable("UNIT_PROBE_PORT", "65111");
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.AddSinapsiMcpServer("example-mcp", "0.1.0").WithHttpTransport();
            using var app = builder.Build();

            app.MapSinapsiMcp(envPrefix: "UNIT_PROBE", defaultPort: 9999);

            Assert.Contains("http://127.0.0.1:65111", app.Urls);
        }
        finally
        {
            Environment.SetEnvironmentVariable("UNIT_PROBE_HOST", null);
            Environment.SetEnvironmentVariable("UNIT_PROBE_PORT", null);
        }
    }

    [Fact]
    public void MapSinapsiMcp_falls_back_to_default_port()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddSinapsiMcpServer("example-mcp", "0.1.0").WithHttpTransport();
        using var app = builder.Build();

        app.MapSinapsiMcp(envPrefix: "UNSET_PREFIX_XYZ", defaultPort: 9214);

        Assert.Contains("http://0.0.0.0:9214", app.Urls);
    }

    /// <summary>
    /// Builds a minimal app that registers the MCP server (stateless or stateful),
    /// runs the same stateless-strip middleware MapSinapsiMcp installs, and exposes
    /// a /probe endpoint that reports whether Mcp-Session-Id survived. The TestHost
    /// server lets us exercise the middleware over a real request pipeline without
    /// binding a socket.
    /// </summary>
    private static WebApplication BuildProbeApp(bool stateless)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddSinapsiMcpServer("example-mcp", "0.1.0")
            .WithHttpTransport(o => o.Stateless = stateless);

        var app = builder.Build();

        // Reuse the production strip logic: stateless servers drop a stray id.
        app.MapSinapsiMcp(envPrefix: "PROBE_NOT_USED", defaultPort: 9000);

        app.MapGet("/probe", (HttpContext ctx) =>
            ctx.Request.Headers.ContainsKey("Mcp-Session-Id")
                ? "session-id-present"
                : "session-id-absent");

        return app;
    }
}
