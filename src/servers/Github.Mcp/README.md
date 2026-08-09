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
- time-tracking → unsupported (Gitea-only) ⇒ `ForgeNotSupportedException`;
- insights → GitHub-only (`GitHubForgeClient.Insights.cs`), where a `stats/*` `202 Accepted`
  with an empty body, a `traffic/*` `403`, and a code-frequency `422` are mapped to structured
  envelopes rather than exceptions.

## Toolset selection

The GitHub host registers the shared **core** surface (users, repos, contents, branches,
commits, issues, pull requests, releases, search, orgs, notifications, webhooks), plus
**`TopicsTools`** and **`ActionsTools`**, plus the GitHub-only **`InsightsTools`**. It **omits
`TimeTrackingTools`** — time-tracking is a Gitea-only surface with no GitHub analogue. Every
shared tool has an identical name + contract to the Forge.Mcp host; the full list is in the
[library README](../../libs/Sinapsi.Forge/README.md#tool-surface-91).

Topics and actions are *optional* on the Forge.Mcp host (`FORGE_TOOLSETS` can drop them for a
forge that lacks the endpoints) but **unconditional here** — GitHub always exposes both. They
were previously absent from `Program.cs` while `GitHubForgeClient` implemented them all along:
a registration gap, not a capability gap, and one that left `set_repo_topics` unreachable so
repo topics had to be set by hand.

`InsightsTools` is the repository "Insights" tab as tools — traffic (`get_traffic_views`,
`get_traffic_clones`, `get_traffic_referrers`, `get_traffic_paths`), activity and contributors
(`list_contributors`, `get_contributor_stats`, `get_commit_activity`, `get_code_frequency`,
`get_participation`, `get_punch_card`), community and dependencies (`get_community_profile`,
`get_sbom`, `list_forks`), plus `get_languages`. All 14 are `ReadOnly`, and all of them need
only the token this host already holds — no PAT scope change. See the
[library README](../../libs/Sinapsi.Forge/README.md#insights-three-upstream-answers-that-are-not-failures)
for the 202 / 403 / 422 envelope contract.

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

The Insights tools add three `ok:false` envelopes that are **answers, not failures** — a
`status:202` + `retry:true` while GitHub warms a `stats/*` cache, a `status:403` when traffic
data needs push access, and a `status:422` when a repo is too large for code frequency. They
carry a `note` instead of an `error`; treat `retry:true` as "ask again in a few seconds".

## Testing

```sh
dotnet test test/Github.Mcp.Tests   # the GitHub adapter (Git Data commit path, topics, dispatch) + error-scrub proof
dotnet test test/Forge.Tests        # the shared tool surface + hardening paths
```
