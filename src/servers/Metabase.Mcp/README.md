# Metabase.Mcp

A personal-lab MCP **server** for a [Metabase](https://www.metabase.com) analytics instance.
It is a thin host over the Metabase REST API: it wires a `MetabaseClient` over a typed
`HttpClient` and exposes the full analytics surface — catalog reads, the query capability
(run native SQL / run a saved card), a generic `request` escape hatch over ANY endpoint, and
database / table / field / card / dashboard / collection / user CRUD — over streamable HTTP
at `/mcp`. Reads are free; mutating tools are flagged `Destructive` so a fronting policy
plane can gate them.

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

Reads (free): `list_databases`, `get_database`, `get_database_metadata`, `list_tables`,
`get_table_metadata`, `get_field`, `list_cards`, `get_card`, `run_native_query`,
`run_card_query`, `list_collections`, `get_collection_items`, `list_dashboards`,
`get_dashboard`, `current_user`, `list_users`, `search`.

Mutations (`Destructive`): `create_database`, `update_database`, `delete_database`,
`sync_database_schema`, `rescan_database_values`, `update_field`, `create_native_card`,
`create_card`, `update_card`, `delete_card`, `create_collection`, `update_collection`,
`create_dashboard`, `update_dashboard`, `delete_dashboard`, `add_card_to_dashboard`,
`request` (any-method escape hatch).

`run_native_query` / `run_card_query` are the primary value of the MCP (read the data
directly); `request` reaches anything the typed tools don't cover (alerts, pulses,
permissions, settings…).

A non-2xx upstream response is surfaced as `{ ok: false, status, error }` rather than being
swallowed into the SDK's generic invoke error.

## Run

```sh
dotnet run -c Release --project src/servers/Metabase.Mcp
# → MCP endpoint on http://0.0.0.0:9221/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is stripped
so it cannot 400 an otherwise-valid request.
