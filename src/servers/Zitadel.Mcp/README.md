# Zitadel.Mcp

A personal-lab MCP **server** for a [ZITADEL](https://zitadel.com) identity instance. It is a
thin host over the ZITADEL management REST API: it wires a `ZitadelClient` over a typed
`HttpClient` and exposes a small **read-only** identity surface (users, projects, OIDC apps)
over streamable HTTP at `/mcp`.

The instance is selected entirely by environment variables — point the same binary at any
ZITADEL deployment by setting `ZITADEL_BASE_URL` and a service token. Nothing about a
specific instance lives in source.

## Configuration

| Env var            | Required | Default     | Purpose |
|--------------------|:--------:|-------------|---------|
| `ZITADEL_BASE_URL` | yes      | —           | Instance root, e.g. `https://auth.example.com`. The `/management/v1/` API paths are appended. |
| `ZITADEL_TOKEN`    | yes      | —           | A service-account / PAT bearer token, held server-side. Inject it at deploy; never bake it in. |
| `ZITADEL_MCP_PORT` | no       | `9220`      | Listen port. |
| `ZITADEL_MCP_HOST` | no       | `0.0.0.0`   | Listen address. |

```sh
ZITADEL_BASE_URL=https://auth.example.com
ZITADEL_TOKEN=<service-account bearer token>
```

## Tools

All read-only:

| Tool             | Purpose |
|------------------|---------|
| `list_users`     | List users in the instance (first page). |
| `get_user`       | Get a single user by id. |
| `list_projects`  | List projects (first page). |
| `list_oidc_apps` | List the applications registered under a project. |
| `get_oidc_app`   | Get a single application within a project. |

A non-2xx upstream response is surfaced as `{ ok: false, status, error }` rather than being
swallowed into the SDK's generic invoke error.

## Run

```sh
dotnet run -c Release --project src/servers/Zitadel.Mcp
# → MCP endpoint on http://0.0.0.0:9220/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is stripped
so it cannot 400 an otherwise-valid request.
