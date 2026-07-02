# Sinapsi.Mcp

Small, hardened .NET helpers for **building and calling
[Model Context Protocol](https://modelcontextprotocol.io) (MCP) servers over HTTP**.
Part of a personal research lab; offered as-is, nuget.org-only, with neutral
env-driven defaults.

It bundles the two things an MCP server written on top of
[`ModelContextProtocol.AspNetCore`](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore)
otherwise hand-rolls each time — the client-side round-trip and the server-side
bootstrap — and validates its public inputs, binds its listen address
fail-closed, and sanitizes any error it surfaces.

## Contents

- [Overview](#overview)
- [Public API reference](#public-api-reference)
- [Configuration](#configuration)
- [Usage](#usage)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Testing](#testing)

## Overview

Two public types:

- **`GatewayMcpClient`** — a minimal Streamable-HTTP JSON-RPC client. Given an
  upstream MCP endpoint, a bearer identity, a tool name and its arguments, it
  performs the three-message round-trip (`initialize` →
  `notifications/initialized` → `tools/call`) and returns the concatenated text
  content of the result, parsing either an SSE `data:` frame or a plain JSON
  body.
- **`SinapsiMcpServerBuilder`** — two extension methods
  (`AddSinapsiMcpServer`, `MapSinapsiMcp`) that remove the duplicated bootstrap
  from an MCP server's `Program.cs`: single-line console logging, an
  infinite-timeout `HttpClient<GatewayMcpClient>`, an env-driven listen address,
  and the `/mcp` route.

The helpers are intentionally thin — they don't hide tool registration or
per-server options, they only remove the duplicated bootstrap. Consumers still
see the SDK types directly.

## Public API reference

### `GatewayMcpClient(HttpClient http)`

Constructs the client over a caller-supplied `HttpClient` (register the typed
client via `AddSinapsiMcpServer`, which disables its 100s timeout so the real
deadline is the caller's own `CancellationToken`).

### `Task<string> CallToolAsync(Uri gateway, string bearerJwt, string toolName, object toolArgs, CancellationToken ct)`

- **Purpose** — call a single tool on an upstream MCP endpoint as a given bearer
  identity; returns the concatenated text content of the result.
- **Inputs** — `gateway` must be an **absolute http/https URI**; `bearerJwt`
  must be non-blank, control-char-free, ≤ 8192 chars; `toolName` must be
  non-blank, control-char-free, ≤ 512 chars. `toolArgs` is serialized as the
  tool arguments. All input checks run **before any network I/O**.
- **Errors** — `ArgumentNullException` / `ArgumentException` for a malformed
  input (message names the offending parameter); `InvalidOperationException` for
  an upstream failure (non-2xx `initialize`/`tools/call`, a missing session id,
  a JSON-RPC error payload, an unparseable body). Every upstream-derived error
  message is routed through the sanitizer (see [Error contract](#error-contract)).

### `IMcpServerBuilder AddSinapsiMcpServer(this WebApplicationBuilder builder, string serverName, string serverVersion)`

- **Purpose** — add the canonical MCP server bootstrap and return the SDK's
  `IMcpServerBuilder` for chaining `.WithHttpTransport()` / `.WithTools<T>()`.
- **Inputs** — `serverName` and `serverVersion` must be non-blank and
  control-char-free.
- **Errors** — `ArgumentException` naming `serverName` / `serverVersion`.

### `WebApplication MapSinapsiMcp(this WebApplication app, string envPrefix, int defaultPort)`

- **Purpose** — map the endpoint at `/mcp` and bind the listen address from
  `<envPrefix>_HOST` / `<envPrefix>_PORT`. When the transport is stateless, it
  also strips an inbound `Mcp-Session-Id` header so a stray session id forwarded
  by an intermediary proxy does not trigger a 400.
- **Inputs** — `envPrefix` must match `[A-Z0-9_]` (≤ 128 chars); `defaultPort`
  must be in `1..65535`.
- **Errors** — `ArgumentException` / `ArgumentOutOfRangeException` for a
  malformed code input; `InvalidOperationException` naming the env var when a
  configured `<envPrefix>_PORT` / `<envPrefix>_HOST` is invalid (fail-closed).

## Configuration

The mapping helper reads its listen address from two env vars composed from the
`envPrefix` you pass to `MapSinapsiMcp`:

| Env var                | Required | Default        | Purpose                                                              |
|------------------------|----------|----------------|----------------------------------------------------------------------|
| `<envPrefix>_HOST`     | No       | `0.0.0.0`      | Listen host. Rejected if it contains whitespace / control chars.     |
| `<envPrefix>_PORT`     | No       | `defaultPort`  | Listen port. Must be a TCP port `1..65535`; otherwise **throws**.    |

Both are **fail-closed**: an invalid configured value throws an
`InvalidOperationException` naming the env var rather than silently composing an
unbindable listen URL.

## Usage

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

Calling an upstream tool as a bearer identity:

```csharp
var result = await client.CallToolAsync(
    new Uri("https://example.test/mcp"),
    bearerJwt: token,
    toolName: "echo",
    toolArgs: new { text = "hello" },
    ct);
```

## Security notes

- **Inputs are validated at the seam.** `CallToolAsync` rejects a relative /
  non-http gateway, a blank / control-char / over-long bearer, and a blank /
  control-char / over-long tool name before any request is sent — a newline or
  NUL can never be smuggled into the outbound `Authorization` header.
- **Config is fail-closed.** A non-numeric or out-of-range `<envPrefix>_PORT`,
  or a `<envPrefix>_HOST` with whitespace / control chars, throws naming the env
  var instead of binding a garbage listen URL.
- **Errors never echo secrets.** Every error string derived from an upstream
  response is sanitized before it leaves the process (see below).
- **No ambient secrets.** The library carries no credentials; the bearer
  identity is passed per call.

## Error contract

Every error message the library surfaces to a caller is routed through a
centralized `Sanitize()` that:

- redacts a PEM **private-key block** (`-----BEGIN ... PRIVATE KEY----- ... END`);
- redacts a bare **NATS seed** (`S[UAONC]…`);
- redacts the value of any
  `password | passwd | secret | token | api-key | bearer | authorization |
  signing-key | nkey | seed` assignment (keeping the key name for diagnosability);
- **length-caps** the message at 2000 chars (`… [truncated]`).

So a leaked token in an upstream body comes back as `[redacted]`, and a
pathological multi-megabyte dump can never balloon the exception. Non-secret
diagnostics (e.g. a `503 service unavailable` status line) pass through
unchanged.

## Testing

Targets **.NET 8**.

```sh
dotnet build
dotnet test
```

The suite in `../Sinapsi.Mcp.Tests` proves the hardening paths fire:

- **Input-validation matrix** (`McpValidationTests`) — missing / control-char /
  over-long bearer + tool name, relative / non-http gateway; and end-to-end that
  `CallToolAsync` rejects a bad input **before any network I/O**.
- **Fail-closed config matrix** (`McpConfigFailClosedTests`) — a non-numeric /
  out-of-range `<envPrefix>_PORT` and a malformed `<envPrefix>_HOST` throw
  **naming the env var**; a malformed `envPrefix` / out-of-range `defaultPort` /
  blank server name/version are rejected.
- **Error-sanitization contract** (`McpSanitizerTests`) — a secret embedded in a
  surfaced message (bearer, token, api-key, password, signing-key, NKey/seed,
  PEM key block) comes back `[redacted]`, and a pathological blob is
  length-capped.
