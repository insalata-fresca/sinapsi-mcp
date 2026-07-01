# Github.Mcp

A personal-lab MCP **server** for GitHub. It is a thin, hardened host over
[`Sinapsi.Forge`](../../libs/Sinapsi.Forge): the `GitHubForgeClient` adapter (raw GitHub REST)
implements the shared `IForgeClient`, so the **same tool surface** that drives Forgejo / Gitea /
Codeberg also drives GitHub — identical tool names and semantics across forges.

## Contents

- [Overview](#overview)
- [Toolset selection](#toolset-selection)
- [Configuration](#configuration)
- [Run](#run)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Testing](#testing)

## Overview

The host reads the GitHub PAT from the environment, constructs a typed `HttpClient` (base
address `https://api.github.com/`, `Authorization: Bearer <PAT>`, the GitHub `Accept` +
`X-GitHub-Api-Version` headers, and a bounded `Timeout`), registers `GitHubForgeClient` as the
`IForgeClient` singleton, and registers the shared `[McpServerTool]` classes from
`Sinapsi.Forge`. All tool behaviour, validation, and error scrubbing live in the library; this
host only wires config → adapter → tools.

The adapter handles the GitHub-specific divergences from the Gitea API:

- `commit_files` → the Git Data/Tree API (blobs → tree → commit → ref) for atomic, byte-safe
  multi-file commits;
- search → the `{ items: [...] }` wrapper;
- release-asset upload → the `uploads.github.com` host;
- time-tracking → unsupported (Gitea-only) ⇒ `ForgeNotSupportedException`.

## Toolset selection

The GitHub host registers the full shared **core** surface (users, repos, contents, branches,
commits, issues, pull requests, releases, search, orgs, notifications, webhooks, repository
topics, and Actions). It **omits `TimeTrackingTools`** — time-tracking is a Gitea-only surface
with no GitHub analogue. Every other tool has an identical name + contract to the Forge.Mcp host;
the full list is in the [library README](../../libs/Sinapsi.Forge/README.md#tool-surface-77).

## Configuration

| Env var | Required | Default | Purpose |
|---------|:--------:|---------|---------|
| `GITHUB_TOKEN` | yes\* | — | GitHub personal access token, held server-side (`Authorization: Bearer`). |
| `GITHUB_PERSONAL_ACCESS_TOKEN` | yes\* | — | Alternative name for the same token. |
| `GITHUB_HTTP_TIMEOUT_MS` | no | `100000` | Hard ceiling on each GitHub HTTP call. Must be an integer in `1..600000` ms; a non-numeric, `<= 0`, or out-of-range value **fails startup**. |
| `GITHUB_MCP_PORT` | no | `9218` | Listen port. |
| `GITHUB_MCP_HOST` | no | `0.0.0.0` | Listen address. |

\* One of `GITHUB_TOKEN` / `GITHUB_PERSONAL_ACCESS_TOKEN` is required. The host **fails to start**
if neither is set. Inject the token at deploy; never bake it in.

## Run

```sh
GITHUB_TOKEN=<github pat> dotnet run -c Release --project src/servers/Github.Mcp
# → MCP endpoint on http://0.0.0.0:9218/mcp
```

The transport is stateless; a fronting proxy's forwarded `Mcp-Session-Id` header is stripped so
it cannot 400 an otherwise-valid request.

## Security notes

- **Fail-closed config.** A token is required; the host throws on startup if neither
  `GITHUB_TOKEN` nor `GITHUB_PERSONAL_ACCESS_TOKEN` is set. `GITHUB_HTTP_TIMEOUT_MS` is validated
  to `1..600000` ms; an invalid value fails startup.
- **No secrets in source.** The PAT is injected at deploy and held server-side on the
  `HttpClient`; it is never read into a tool response.
- **No secret leakage in errors.** Every surfaced upstream string — including a GitHub error body
  baked into a `ForgeApiException` — is scrubbed by the library's `SinapsiForgeErrors.Sanitize`
  (PEM private keys + credential assignments → `[redacted]`, length-capped) before it reaches a
  caller. The RAW HTTP status is computed first, so a redaction can never change the reported
  status.
- **Input validation before side effects.** Every tool validates its URL-bound parameters before
  any HTTP call; a hostile value can neither traverse a URL path nor be smuggled into a request.
- **Bounded HTTP.** Every GitHub call runs under `GITHUB_HTTP_TIMEOUT_MS`; a hung upstream
  surfaces as a structured error rather than pinning a request thread.

## Error contract

Every tool returns a JSON object. On a validation failure it returns
`{ "ok": false, "status": null, "error": "<reason>" }` with **no HTTP call made**; on an upstream
failure, `{ "ok": false, "status": <http-status|null>, "error": "<scrubbed>" }`. See the
[library error contract](../../libs/Sinapsi.Forge/README.md#error-contract).

## Testing

```sh
dotnet test test/Github.Mcp.Tests   # the GitHub adapter (Git Data commit path, topics, dispatch) + error-scrub proof
dotnet test test/Forge.Tests        # the shared tool surface + hardening paths
```
