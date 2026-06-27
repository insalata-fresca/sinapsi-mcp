# Zitadel.Mcp

A personal-lab MCP **server** for a [ZITADEL](https://zitadel.com) identity instance. It is a
thin host over the ZITADEL management REST API: it wires a `ZitadelClient` over a typed
`HttpClient` and exposes the full identity-management surface (users, projects, OIDC apps,
and the machine-identity lifecycle) over streamable HTTP at `/mcp`. Reads are free; mutating
tools are flagged `Destructive` so a fronting policy plane can gate them.

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
| `AGENT_KEY_DIR`    | no       | `/agent-keys` | Host-side directory `create_machine_key` writes a machine user's JSON private key into (mode 0640). The key is never returned to the caller. |

```sh
ZITADEL_BASE_URL=https://auth.example.com
ZITADEL_TOKEN=<service-account bearer token>
```

## Tools

Reads (free):

| Tool             | Purpose |
|------------------|---------|
| `list_users`     | List users in the instance (first page). |
| `get_user`       | Get a single user by id. |
| `list_projects`  | List projects (first page). |
| `list_oidc_apps` | List the applications registered under a project. |
| `get_oidc_app`   | Get a single application within a project. |

Mutations (`Destructive`):

| Tool                     | Purpose |
|--------------------------|---------|
| `create_project`         | Create a new project. |
| `create_oidc_app`        | Create an OIDC application (= client). |
| `update_oidc_app_config` | Update an OIDC app's config (only provided fields are sent). |
| `delete_oidc_app`        | Delete an application by id. |
| `regenerate_oidc_secret` | Rotate the OIDC client secret (SENSITIVE — response carries the new secret). |
| `create_machine_user`    | Create a machine (service) user. |
| `update_machine_user`    | Update a machine user (name / description / access-token type). |
| `delete_machine_user`    | Delete a user by id (RemoveUser; irreversible). |
| `create_pat`             | Issue a Personal Access Token for a machine user (SENSITIVE). |
| `create_machine_key`     | Issue a machine user's JSON key and write it host-side to `AGENT_KEY_DIR` (the key is never returned — only `{ok, userId, keyId, path, bytes}`). |

The machine-identity tools (`create_machine_user` / `create_machine_key` / `create_pat`)
back the homelab M2M secret-delivery flow — mint an agent identity + its credential by-the-books.

A non-2xx upstream response is surfaced as `{ ok: false, status, error }` rather than being
swallowed into the SDK's generic invoke error.

## Run

```sh
dotnet run -c Release --project src/servers/Zitadel.Mcp
# → MCP endpoint on http://0.0.0.0:9220/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is stripped
so it cannot 400 an otherwise-valid request.
