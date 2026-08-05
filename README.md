# sinapsi-mcp

A personal research lab for agentic-infrastructure patterns — in code you can compile and run.

This repository is a workbench. It collects small, self-contained .NET
[Model Context Protocol](https://modelcontextprotocol.io) (MCP) servers and the
shared libraries they lean on — written to learn how language-model agents and the
systems around them actually behave when you give them real work to do.

It is the sibling of my written notes and design essays,
**[sinapsi-design](https://github.com/insalata-fresca/sinapsi-design)**: where those
reason about agentic software in prose, this one reasons about it in code. There is no
product here, no service to sign up for, and nothing to buy.

**Thirteen MCP servers and the shared .NET libraries they lean on, running in my home
lab.** Every server is the same shape — a thin, *hardened* host over one real system
(a git forge, a certificate authority, an identity server, a secrets store, an SSH
transport…), exposing a small, bounded tool surface over streamable HTTP at `/mcp`:
parameters validated before any upstream call, credentials held server-side, upstream
errors scrubbed, hard per-request timeouts. That recurring shape — *give an agent a
bounded, audited door to a system instead of raw access* — is the thing these
experiments actually explore.

## Start here: the trust-plane servers

If you read only a few, read these — they're where the [design
essays](https://github.com/insalata-fresca/sinapsi-design) turn into code.

- **[`Sshgw.Mcp`](src/servers/Sshgw.Mcp)** — an agent's SSH access as a *bounded door*:
  a per-server command whitelist and read-path policy, each command classified by
  effect (read / write / secret-read) and the resulting decision emitted to an audit
  stream. The gateway-and-policy pattern from *[The Tool
  Plane](https://github.com/insalata-fresca/sinapsi-design/blob/main/10-the-tool-plane.md)*
  and *[The Trust
  Plane](https://github.com/insalata-fresca/sinapsi-design/blob/main/03-the-trust-plane.md)*.
- **[`Sinapsi.SentinelConsole`](src/servers/Sinapsi.SentinelConsole)** — the inspection
  surface: one live screen of the authorization decisions across the layers, so reading
  the posture never means grepping across repos and hosts. The observability argument of
  *[The Decision
  Trail](https://github.com/insalata-fresca/sinapsi-design/blob/main/12-the-decision-trail.md)*.
- **[`StepCa.Mcp`](src/servers/StepCa.Mcp)**, **[`Zitadel.Mcp`](src/servers/Zitadel.Mcp)**,
  **[`Infisical.Mcp`](src/servers/Infisical.Mcp)** — certificates, identity, and runtime
  secrets behind bounded surfaces: identity per actor and secrets delivered at runtime
  rather than pasted. The machinery behind *[Identity and
  Secrets](https://github.com/insalata-fresca/sinapsi-design/blob/main/11-identity-and-secrets.md)*.

## All the servers

Each lives in its own folder under `src/servers/` with its own README.

| Server | What it does |
|---|---|
| [`Sshgw.Mcp`](src/servers/Sshgw.Mcp) | Fronts a set of SSH hosts: a per-server command whitelist + read-path policy, each command classified by effect and its authorization decision emitted to an audit stream. |
| [`Sinapsi.SentinelConsole`](src/servers/Sinapsi.SentinelConsole) | The inspection surface of the authorization plane — one live screen of what's being allowed, denied, or escalated across the layers. |
| [`StepCa.Mcp`](src/servers/StepCa.Mcp) | Fronts a [`step-ca`](https://smallstep.com/docs/step-ca/) internal certificate authority over the `step` CLI — issue and inspect certs behind a bounded 6-tool surface. |
| [`Zitadel.Mcp`](src/servers/Zitadel.Mcp) | Fronts a [ZITADEL](https://zitadel.com) identity instance (management API): machine users, OIDC apps, grants — bearer held server-side, every parameter validated. |
| [`Infisical.Mcp`](src/servers/Infisical.Mcp) | Issues and stores secrets in an [Infisical](https://infisical.com) project behind a 4-tool surface — runtime secret delivery, no pasted credentials. |
| [`Sinapsi.Indexer`](src/servers/Sinapsi.Indexer) | Real-time, searchable memory for an agent over source-of-truth git repos: indexes their markdown, stays fresh from NATS git-push events, Postgres full-text under the hood. |
| [`Forge.Mcp`](src/servers/Forge.Mcp) | One MCP tool surface over any Gitea-API git forge (Forgejo / Gitea / Codeberg), via a shared `IForgeClient`. |
| [`Github.Mcp`](src/servers/Github.Mcp) | The *same* forge tool surface driven against GitHub's REST API — identical tool names across every forge. |
| [`Gdrive.Mcp`](src/servers/Gdrive.Mcp) | A self-hosted Google Drive CRUD server that owns the full file lifecycle — including the `update`/`delete` most managed connectors omit. |
| [`Gemini.Mcp`](src/servers/Gemini.Mcp) | Wraps the authenticated Google `gemini` CLI as MCP tools, bounded by a hard timeout. |
| [`Metabase.Mcp`](src/servers/Metabase.Mcp) | Fronts a [Metabase](https://www.metabase.com) analytics instance: catalog reads plus native-SQL and saved-card queries over a typed client. |
| [`SageCouncil.Mcp`](src/servers/SageCouncil.Mcp) | Convenes a small multi-model "council": fans one hard question to three independent model members in parallel and returns their views. |
| [`OpenWrtForum.Mcp`](src/servers/OpenWrtForum.Mcp) | Wraps a [Discourse](https://www.discourse.org/) forum's REST API (defaults to the public OpenWrt forum) as a small MCP tool set. |

## Shared libraries

Under `src/libs/`, factored out so a server depends only on the versioned package it asks for:

- **`Sinapsi.Mcp`** — the hardened-host helpers every server is built on (hosting, validation, error scrubbing, timeouts).
- **`Sinapsi.Forge`** — the shared `IForgeClient` and forge tool surface that `Forge.Mcp` and `Github.Mcp` both drive.
- **`Sinapsi.Nats`** — NATS / JetStream client helpers (the event spine the indexer and audit stream ride on).
- **`Sinapsi.AgentJwt`** — agent identity/JWT helpers.

## Layout

```
src/
  libs/       shared .NET libraries, consumed as versioned packages
  servers/    one MCP server per subfolder, each with its own README
test/         unit tests
Sinapsi.Mcp.sln
```

## Building

Everything targets **.NET 8**.

```sh
dotnet restore
dotnet build
```

Shared libraries are packed and consumed as NuGet packages rather than wired together
with project references, so each server depends only on the published version it asks
for. The lab serves these packages from its own Forgejo NuGet feed; see `nuget.config`
for the source list.

## Continuous integration

A small Forgejo Actions workflow (`.forgejo/workflows/ci.yml`) restores, builds and
tests on every pull request, and on a version tag (`v*`) packs the libraries and pushes
them to the lab's Forgejo NuGet feed. That feed is internal; publishing the *packages*
to a public feed is intentionally left out for now (see `TODO.md`).

## About this repository

This is a **curated, read-only public snapshot** of exploratory code written for my own
learning. It changes when I learn something, and it's offered as-is under the terms in
[`LICENSE`](LICENSE). No employer or client material, and no operational specifics of any
real system, appear here.
