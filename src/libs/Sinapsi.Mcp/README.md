# Sinapsi.Mcp

Small .NET helpers for building and calling [Model Context Protocol](https://modelcontextprotocol.io)
(MCP) servers over HTTP. Part of a personal research lab; offered as-is.

It bundles two things that an MCP server written on top of
[`ModelContextProtocol.AspNetCore`](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore)
otherwise hand-rolls each time.

## `GatewayMcpClient`

A minimal Streamable-HTTP JSON-RPC client. Given an upstream MCP endpoint, a bearer
token, a tool name and its arguments, it performs the three-message round-trip —
`initialize` → `notifications/initialized` → `tools/call` — and returns the
concatenated text content of the result. It parses either an SSE `data:` frame or a
plain JSON body.

```csharp
var result = await client.CallToolAsync(
    new Uri("https://example.test/mcp"),
    bearerJwt: token,
    toolName: "echo",
    toolArgs: new { text = "hello" },
    ct);
```

## `SinapsiMcpServerBuilder`

Two extension methods that remove the bootstrap boilerplate from an MCP server's
`Program.cs`:

```csharp
using Sinapsi.Mcp;

var builder = WebApplication.CreateBuilder(args);

builder
    .AddSinapsiMcpServer("example-mcp", "0.1.0")
    .WithHttpTransport(o => o.Stateless = true) // statelessness is the caller's choice
    .WithTools<MyTool>();

var app = builder.Build();
app.MapSinapsiMcp(envPrefix: "EXAMPLE_MCP", defaultPort: 9200).Run();
```

- `AddSinapsiMcpServer(name, version)` configures single-line console logging, registers
  an infinite-timeout `HttpClient<GatewayMcpClient>` (so the real deadline is the
  caller's own `CancellationToken` rather than the default 100s `HttpClient` timeout),
  and calls `AddMcpServer` with the given server info. It returns the SDK's
  `IMcpServerBuilder`, so you keep chaining `.WithHttpTransport(...)` and
  `.WithTools<T>()` as usual.
- `MapSinapsiMcp(envPrefix, defaultPort)` maps the endpoint at `/mcp` and binds the
  listen address from `<envPrefix>_HOST` (default `0.0.0.0`) and `<envPrefix>_PORT`
  (default `defaultPort`). When the configured transport is **stateless**, it also
  strips any inbound `Mcp-Session-Id` header, so a stray session id forwarded by an
  intermediary proxy does not trigger a 400 in stateless mode.

The helpers are intentionally thin: they don't hide tool registration or per-server
options, they only remove the duplicated bootstrap. Consumers still see the SDK types
directly.

## Building

Targets **.NET 8**.

```sh
dotnet build
dotnet test
```
