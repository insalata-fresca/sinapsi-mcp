# SageCouncil.Mcp

A personal-lab MCP **server** that convenes a small multi-AI "council" on a
hard, articulated question — a real design, architecture, trade-off, or
second-opinion problem you cannot confidently resolve alone.

It fans out the same prompt, in parallel, to three research members:

- **claude-research** — runs against an HTTP *agent backend* exposing a simple
  session API (`POST /v1/sessions`, `POST .../messages`, `DELETE ...`).
- **gemini-research** — calls a `gemini_research` tool through an MCP gateway and
  polls `gemini_get_status` until the async research task finishes.
- **chatgpt-research** — calls a `codex_codex` tool through the same gateway in a
  read-only research sandbox.

Each member authenticates with its own machine identity (an RFC 7523 JWT-bearer
assertion minted by the in-repo [`Sinapsi.AgentJwt`](../../libs/Sinapsi.AgentJwt)
library — **not** a vendored copy), is capped by a per-member wall-clock
deadline, and a final synthesis pass reconciles the perspectives into one
grounded answer. A consult is intentionally long-running (members do genuine
multi-step research), so the `consult` tool is **asynchronous**: it returns a
`job_id` immediately and you collect the result later with `consult_result`.

This is exploratory code written for personal learning. There is no product
here — nothing to sign up for and nothing to buy.

## Tools

| Tool             | What it does |
|------------------|--------------|
| `consult`        | Dispatch the council on a `prompt` with a `focus` persona and an optional `members` roster. Returns a `job_id` (status `running`). |
| `consult_result` | Poll a `job_id`: `running`, `done` (full members + synthesis), or `error`. Results retained ~1 hour. |

`focus` selects the research mandate the members adopt: `general` (default),
`code-review`, `architecture`, `second-opinion`, `deep-research`, or `design`.
The roster defaults to all three members. When the `focus` is `design`, the
members are asked to return strict JSON and the synthesis pass merges those JSON
objects (rather than writing prose) so a downstream consumer gets clean output.

## Configuration

Everything is env-driven with neutral local defaults — point it at your own
infrastructure. No value is baked to any specific deployment.

| Env var                    | Default                          | Purpose |
|----------------------------|----------------------------------|---------|
| `AGENT_BACKEND_URL`        | `http://127.0.0.1:8088`          | The agent backend the `claude-research` member spawns sessions on. |
| `GATEWAY_URL`              | `http://127.0.0.1:8443/mcp`      | The MCP gateway the gemini/chatgpt members call through. |
| `AGENT_MODEL`              | `claude-sonnet-4-6`              | Model the `claude-research` session is created with. |
| `OIDC_ISSUER`              | `https://oidc.example`           | OIDC issuer for the JWT-bearer token exchange (`Sinapsi.AgentJwt.Issuer`). |
| `OIDC_AUDIENCE_PROJECT_ID` | *(empty)*                        | Audience project id the minted token is scoped to (`Sinapsi.AgentJwt.AudienceProjectId`). |
| `AGENT_KEY_DIR`            | `/etc/agent-jwt/keys`            | Directory of per-member JWK files (`<agent>.json`), mounted read-only. |
| `JWT_TTL_MIN`              | `15`                             | Token TTL in minutes (cache TTL is this minus one). |
| `COUNCIL_CLAUDE_AGENT`     | `agent-council-claude`           | Identity name (JWK filename) for the claude member. |
| `COUNCIL_GEMINI_AGENT`     | `agent-council-gemini`           | Identity name for the gemini member. |
| `COUNCIL_CHATGPT_AGENT`    | `agent-council-chatgpt`          | Identity name for the chatgpt member. |
| `PERSONA_DIR`              | `/etc/sage-council-mcp/personas` | Optional overlay: drop a `<focus>.md` file to add/override a persona — no recompile. |
| `SAGE_TIMEOUT_MS`          | `1800000` (30 min)               | Per-outbound-call ceiling (safety net for a hung backend). |
| `SAGE_MEMBER_TIMEOUT_MS`   | `1500000` (25 min)               | Per-member wall-clock deadline. |
| `SAGE_COUNCIL_MCP_HOST`    | `0.0.0.0`                        | Listen address. |
| `SAGE_COUNCIL_MCP_PORT`    | `9212`                           | Listen port. |

The OIDC issuer + audience + key-dir + TTL knobs are read by
`Sinapsi.AgentJwt.AgentJwtOptions.FromEnvironment()`; see that library's README
for the JWK file shape and the token-exchange flow.

## Run

```sh
dotnet run -c Release --project src/servers/SageCouncil.Mcp
# → MCP endpoint on http://0.0.0.0:9212/mcp
```

It is a thin host over two in-repo libraries —
[`Sinapsi.Mcp`](../../libs/Sinapsi.Mcp) (server bootstrap + the upstream
`GatewayMcpClient`) and [`Sinapsi.AgentJwt`](../../libs/Sinapsi.AgentJwt) (the
per-member JWT-bearer minter) — both referenced by project so the server builds
with no private NuGet feed (only nuget.org).
