# Metabase.Mcp

A personal-lab MCP **server** for a [Metabase](https://www.metabase.com) analytics instance.
It is a thin host over the Metabase REST API: it wires a `MetabaseClient` over a typed
`HttpClient` and exposes a small **read-only** catalog surface (databases, collections,
saved questions) over streamable HTTP at `/mcp`.

The instance is selected entirely by environment variables — point the same binary at any
Metabase deployment by setting `METABASE_BASE_URL` and an API key. Nothing about a specific
instance lives in source.

## Configuration

| Env var             | Required | Default     | Purpose |
|---------------------|:--------:|-------------|---------|
| `METABASE_BASE_URL` | yes      | —           | Instance root, e.g. `https://metrics.example.com`. The `/api/` paths are appended. |
| `METABASE_API_KEY`  | yes      | —           | A Metabase API key, held server-side, sent as the `X-API-KEY` header. Inject it at deploy; never bake it in. |
| `METABASE_MCP_PORT` | no       | `9221`      | Listen port. |
| `METABASE_MCP_HOST` | no       | `0.0.0.0`   | Listen address. |

```sh
METABASE_BASE_URL=https://metrics.example.com
METABASE_API_KEY=<metabase api key>
```

## Tools

All read-only:

| Tool                | Purpose |
|---------------------|---------|
| `list_databases`    | List the configured databases. |
| `list_collections`  | List the collections. |
| `list_cards`        | List the saved questions (cards). |
| `get_card`          | Get a single saved question (card) by id. |

A non-2xx upstream response is surfaced as `{ ok: false, status, error }` rather than being
swallowed into the SDK's generic invoke error.

## Run

```sh
dotnet run -c Release --project src/servers/Metabase.Mcp
# → MCP endpoint on http://0.0.0.0:9221/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is stripped
so it cannot 400 an otherwise-valid request.
