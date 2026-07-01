using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace Sinapsi.Mcp;

/// <summary>
/// Server-hosting helpers for an HTTP MCP server. Consolidates the bootstrap an
/// MCP server otherwise repeats (single-line console logging, an infinite
/// HttpClient timeout for the upstream <see cref="GatewayMcpClient"/>, an
/// env-driven listen address, and the <c>/mcp</c> route).
///
/// Usage:
/// <code>
/// var b = WebApplication.CreateBuilder(args)
///     .AddSinapsiMcpServer("example-mcp", "0.1.0")
///     .WithHttpTransport()
///     .WithTools&lt;MyTool&gt;();
///
/// var app = b.Build();
/// app.MapSinapsiMcp(envPrefix: "EXAMPLE_MCP", defaultPort: 9200).Run();
/// </code>
///
/// The helpers are intentionally thin — they don't hide tool registration or
/// per-server options; they only remove the duplicated bootstrap. Built on
/// <c>ModelContextProtocol.AspNetCore</c>; consumers still see the SDK directly.
/// </summary>
public static class SinapsiMcpServerBuilder
{
    /// <summary>
    /// Add the canonical MCP server bootstrap to a <see cref="WebApplicationBuilder"/>:
    /// single-line console logging + an infinite-timeout HttpClient for
    /// <see cref="GatewayMcpClient"/> (so long-running upstream calls are governed
    /// only by the caller's own <see cref="CancellationToken"/>, not by the default
    /// HttpClient timeout) + <c>AddMcpServer</c> wired with the supplied server info.
    /// Returns the SDK's <see cref="IMcpServerBuilder"/> so consumers can chain
    /// <c>.WithHttpTransport()</c> and <c>.WithTools&lt;T&gt;()</c>.
    /// </summary>
    /// <param name="builder">the host builder</param>
    /// <param name="serverName">MCP <c>ServerInfo.Name</c> (e.g. "example-mcp")</param>
    /// <param name="serverVersion">MCP <c>ServerInfo.Version</c> (semver; informational only)</param>
    public static IMcpServerBuilder AddSinapsiMcpServer(
        this WebApplicationBuilder builder,
        string serverName,
        string serverVersion)
    {
        // Validate the caller-supplied server identity at the seam so a blank or
        // control-char-laced name/version is rejected up front rather than baked
        // into the advertised ServerInfo.
        serverName = McpValidation.RequireText(serverName, nameof(serverName));
        serverVersion = McpValidation.RequireText(serverVersion, nameof(serverVersion));

        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

        // HttpClient's default 100s timeout silently caps long-running calls
        // regardless of the CancellationToken passed to SendAsync (whichever is
        // shorter wins). Disable it here so the real deadline is the per-call
        // (linked) CancellationToken the caller supplies.
        builder.Services.AddHttpClient<GatewayMcpClient>(c => c.Timeout = Timeout.InfiniteTimeSpan);

        return builder.Services.AddMcpServer(o => o.ServerInfo = new()
        {
            Name = serverName,
            Version = serverVersion,
        });
    }

    /// <summary>
    /// Map the MCP endpoint at <c>/mcp</c> and bind the listen address from
    /// env vars: <c>&lt;envPrefix&gt;_HOST</c> (default <c>0.0.0.0</c>) +
    /// <c>&lt;envPrefix&gt;_PORT</c> (default <paramref name="defaultPort"/>).
    /// Returns the app so the caller can chain <c>.Run()</c>.
    /// </summary>
    /// <param name="app">the built host</param>
    /// <param name="envPrefix">env-var prefix (uppercase, e.g. "EXAMPLE_MCP")</param>
    /// <param name="defaultPort">fallback port when <c>&lt;envPrefix&gt;_PORT</c> is unset</param>
    public static WebApplication MapSinapsiMcp(
        this WebApplication app,
        string envPrefix,
        int defaultPort)
    {
        // Validate the binding inputs at the seam: a well-formed env-var prefix
        // (so "<prefix>_HOST"/"<prefix>_PORT" compose safely) and an in-range
        // default port.
        envPrefix = McpValidation.RequireEnvPrefix(envPrefix);
        defaultPort = McpValidation.RequireDefaultPort(defaultPort);

        // ModelContextProtocol.AspNetCore in stateless mode rejects any request
        // that carries an Mcp-Session-Id header (400 "The Mcp-Session-Id header is
        // not supported in stateless mode"). An intermediary HTTP proxy in front of
        // the server may forward the client's session id even when this backend is
        // stateless, which would 400 otherwise-valid requests (e.g. a tools/list
        // probe). When the registered transport is stateless, strip any inbound
        // Mcp-Session-Id header before mapping the endpoint. Stateful servers are
        // left untouched — they thread a real session id.
        var transport = app.Services.GetService<IOptions<HttpServerTransportOptions>>();
        if (transport?.Value.Stateless == true)
        {
            app.Use(async (context, next) =>
            {
                context.Request.Headers.Remove("Mcp-Session-Id");
                await next();
            });
        }

        app.MapMcp("/mcp");

        // Bind the listen address fail-closed: a configured <prefix>_PORT that is
        // non-numeric or out of the 1..65535 TCP range THROWS naming the env var
        // (rather than silently composing an unbindable listen URL from garbage),
        // and a <prefix>_HOST carrying whitespace/control chars is likewise
        // rejected. When either var is unset the neutral default is used.
        var host = McpValidation.ReadHost($"{envPrefix}_HOST", "0.0.0.0");
        var port = McpValidation.ReadPort($"{envPrefix}_PORT", defaultPort);
        app.Urls.Add($"http://{host}:{port}");

        return app;
    }
}
