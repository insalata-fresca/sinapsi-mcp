# Forge.Mcp

A personal-lab MCP **server** for any Gitea-API git forge. It is a thin host over
[`Sinapsi.Forge`](../../libs/Sinapsi.Forge): it wires a `GiteaForgeClient` as the
`IForgeClient` and exposes the shared tool surface over streamable HTTP at `/mcp`.

**One binary serves both Forgejo and Codeberg.** They are *not* forked code — the
deployment is selected entirely by environment variables, and an optional `FORGE_TOOLSETS`
knob drops tool groups a given instance does not expose. This is the whole point of the
host: the forge differences live in config, not in source.

## Configuration

| Env var          | Required | Default     | Purpose |
|------------------|:--------:|-------------|---------|
| `FORGE_BASE_URL` | yes      | —           | Forge root, e.g. `https://forge.example.com`. The `/api/v1/` suffix is appended. |
| `FORGE_TOKEN`    | yes      | —           | A Gitea/Forgejo personal access token, held server-side. Inject it at deploy; never bake it in. |
| `FORGE_NAME`     | no       | `forgejo`   | Logical backend name (server-info + diagnostics), e.g. `forgejo` or `codeberg`. |
| `FORGE_TOOLSETS` | no       | all enabled | Comma list to opt out of optional tool groups on a forge that lacks them, e.g. `-topics,-actions`. |
| `FORGE_MCP_PORT` | no       | `9219`      | Listen port. Also overridable via `FORGE_MCP_PORT` (the `MapSinapsiMcp` default). |
| `FORGE_MCP_HOST` | no       | `0.0.0.0`   | Listen address. |

### Forgejo profile (full surface)

```sh
FORGE_NAME=forgejo
FORGE_BASE_URL=https://forge.example.com
FORGE_TOKEN=<forgejo personal access token>
# FORGE_TOOLSETS unset → topics + actions + everything enabled
```

### Codeberg profile (same binary, optional groups off)

Codeberg is a Gitea instance, so the same adapter drives it — only the base URL differs,
and (on instances without those endpoints) the repository-topics and Actions tool groups
are opted out:

```sh
FORGE_NAME=codeberg
FORGE_BASE_URL=https://codeberg.example.org
FORGE_TOKEN=<codeberg personal access token>
FORGE_TOOLSETS=-topics,-actions
```

## Run

```sh
dotnet run -c Release --project src/servers/Forge.Mcp
# → MCP endpoint on http://0.0.0.0:9219/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is stripped
so it cannot 400 an otherwise-valid request.
