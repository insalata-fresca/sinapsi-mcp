# Forge.Mcp

A personal-lab MCP **server** for any Gitea-API git forge (Forgejo, Gitea, Codeberg). It is a
thin, hardened host over [`Sinapsi.Forge`](../../libs/Sinapsi.Forge): it wires a
`GiteaForgeClient` as the shared `IForgeClient` and exposes the common forge tool surface over
streamable HTTP at `/mcp`.

**One binary serves Forgejo, Gitea, and Codeberg.** They are *not* forked code — the deployment
is selected entirely by environment variables, and an optional `FORGE_TOOLSETS` knob drops the
tool groups a given instance does not expose. The forge differences live in config, not in
source.

## Contents

- [Overview](#overview)
- [Toolset selection](#toolset-selection)
- [Configuration](#configuration)
- [Run](#run)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Testing](#testing)

## Overview

The host binds config from the environment (`ForgeConfig.FromEnv`), constructs a typed
`HttpClient` (base address `<FORGE_BASE_URL>/api/v1/`, `Authorization: token <PAT>`, a bounded
`Timeout`), registers `GiteaForgeClient` as the `IForgeClient` singleton, and registers the
shared `[McpServerTool]` classes from `Sinapsi.Forge`. All tool behaviour, validation, and error
scrubbing live in the library; this host only wires config → adapter → tools.

## Toolset selection

The full Gitea surface is registered by default. Two optional groups are opt-out per forge via
`FORGE_TOOLSETS`, plus one Gitea-only group that is always on for this host:

| Group | Tools | Registered |
|-------|-------|:----------:|
| Core (users, repos, contents, branches, commits, issues, PRs, releases, search, orgs, notifications, webhooks) | 63 tools | always |
| `TimeTrackingTools` | `list_issue_tracked_times`, `add_issue_time` | always (Gitea-only surface) |
| `TopicsTools` | `list_repo_topics`, `add_repo_topic`, `remove_repo_topic`, `set_repo_topics` | on unless `FORGE_TOOLSETS=-topics` |
| `ActionsTools` | `dispatch_workflow`, `list_workflow_runs` | on unless `FORGE_TOOLSETS=-actions` |

`FORGE_TOOLSETS` is a comma list of `+group` / `-group` tokens (e.g. `-topics,-actions` for a
Codeberg instance lacking those endpoints). The full shared tool surface is documented in the
[library README](../../libs/Sinapsi.Forge/README.md#tool-surface-77).

## Configuration

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `FORGE_BASE_URL` | yes | — | Forge root, e.g. `https://forge.example.com`. The `/api/v1/` suffix is appended. Server **fails to start** if unset. |
| `FORGE_TOKEN` | yes | — | A Gitea/Forgejo personal access token, held server-side. Inject at deploy; never bake it in. Server **fails to start** if unset. |
| `FORGE_NAME` | no | `forgejo` | Logical backend name (MCP server name + diagnostics), e.g. `forgejo`, `gitea`, `codeberg`. |
| `FORGE_TOOLSETS` | no | all enabled | Comma list to opt out of optional tool groups, e.g. `-topics,-actions`. |
| `FORGE_HTTP_TIMEOUT_MS` | no | `100000` | Hard ceiling on each forge HTTP call. Must be an integer in `1..600000` ms; a non-numeric, `<= 0`, or out-of-range value **fails startup** (rather than binding an unbounded or instantly-timing-out client). |
| `FORGE_MCP_PORT` | no | `9219` | Listen port. |
| `FORGE_MCP_HOST` | no | `0.0.0.0` | Listen address. |

### Forgejo / Gitea profile (full surface)

```sh
FORGE_NAME=forgejo
FORGE_BASE_URL=https://forge.example.com
FORGE_TOKEN=<forgejo personal access token>
# FORGE_TOOLSETS unset → topics + actions + everything enabled
```

### Codeberg profile (same binary, optional groups off)

Codeberg is a Gitea instance, so the same adapter drives it — only the base URL differs, and (on
instances without those endpoints) the repository-topics and Actions tool groups are opted out:

```sh
FORGE_NAME=codeberg
FORGE_BASE_URL=https://codeberg.example.org
FORGE_TOKEN=<codeberg personal access token>
FORGE_TOOLSETS=-topics,-actions
```

## Run

```sh
FORGE_BASE_URL=https://forge.example.com \
FORGE_TOKEN=<pat> \
dotnet run -c Release --project src/servers/Forge.Mcp
# → MCP endpoint on http://0.0.0.0:9219/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is stripped so
it cannot 400 an otherwise-valid request.

## Security notes

- **Fail-closed config.** `FORGE_BASE_URL` and `FORGE_TOKEN` are required; the host throws on
  startup if either is missing. `FORGE_HTTP_TIMEOUT_MS` is validated to `1..600000` ms; an
  invalid value fails startup rather than binding a footgun timeout.
- **No secrets in source.** The forge PAT is injected at deploy and held server-side on the
  `HttpClient` (`Authorization: token <PAT>`); it is never read into a tool response.
- **No secret leakage in errors.** Every surfaced upstream string is scrubbed by the library's
  `SinapsiForgeErrors.Sanitize` (PEM private keys + `password|secret|token|api-key|bearer|
  authorization` assignments → `[redacted]`, length-capped) before it reaches a caller. The RAW
  HTTP status is computed first, so a redaction can never change the reported status.
- **Input validation before side effects.** Every tool validates its URL-bound parameters
  (owner/repo/username/org/ref/path/limit/id) before any HTTP call; a hostile value can neither
  traverse a URL path nor be smuggled into a request.
- **Bounded HTTP.** Every forge call runs under `FORGE_HTTP_TIMEOUT_MS`; a hung upstream surfaces
  as a structured error rather than pinning a request thread.

## Error contract

Every tool returns a JSON object. On a validation failure it returns
`{ "ok": false, "status": null, "error": "<reason>" }` with **no HTTP call made**; on an upstream
failure, `{ "ok": false, "status": <http-status|null>, "error": "<scrubbed>" }`. See the
[library error contract](../../libs/Sinapsi.Forge/README.md#error-contract).

## Testing

```sh
dotnet test test/Forge.Mcp.Tests   # host config: forgejo/codeberg differentiation + fail-closed timeout matrix
dotnet test test/Forge.Tests       # the shared tool surface + hardening paths (validation, scrub, guard, timeout)
```
