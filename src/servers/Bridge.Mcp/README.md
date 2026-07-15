# Bridge.Mcp

The operator's **claude.ai connector** — a curated Sinapsi.Mcp Streamable-HTTP server
(`/mcp`) exposing a hand-coded, auth-gated set of tools to a Claude Project. It is NOT a
generic gateway passthrough: every tool is written and scoped here. Deployed on
CT145-bridge-mcp behind `https://bridge.insalata-fresca.ch/mcp`.

Auth is a legacy static bearer (Phase 1-3, `BRIDGE_BEARER_TOKEN`) or a Zitadel OIDC JWT
(Phase 4, RS256/ES256, RFC 9728 discovery at `/.well-known/oauth-protected-resource`).
Each tool calls `AuthService.Authorize(scope, bucket, limit)` at its top; the legacy
bearer / trusted JWT is auto-granted the non-sensitive scope surface.

## Tool inventory

| Group | Tools | Scope | Backend + transport |
|---|---|---|---|
| Read | `list_workspaces`, `list_files`, `read_file`, `check_inbox` | `bridge:read:documents` | Forgejo REST (in-process) |
| Grep | `search_documents`, `lookup_fact` | `bridge:read:facts` | local repo cache |
| Context pack | `get_context_pack` | `bridge:context_pack` | local grep engine |
| Deposit | `deposit_session_summary`, `deposit_document`, `deposit_artifact` | `bridge:deposit` | Forgejo REST |
| Stateful | `list_recent_additions`, `mark_inbox_read` | `bridge:read:documents` | Forgejo REST |
| Career | `career_search` | `bridge:read:documents` | Sinapsi.Indexer `GET /search` (REST bearer) |
| Cervello open-points | `cervello_open_points_list`, `cervello_open_points_answer` | `bridge:cervello:{read,deposit}` | CT146 :8147 (REST bearer via PEP URL) |
| Cervello dialogue | `cervello_context_pack`, `cervello_search`, `cervello_get`, `cervello_timeline_walk`, `cervello_capture_fact`, `cervello_set_goal`, `cervello_link_evidence` | `bridge:cervello:{read,deposit}` | CT146 :8147 / indexer :8009 (REST bearer) |
| **Personal health** | **`health_list_weight`, `health_list_sleep`, `health_list_steps`, `health_list_datapoints`, `health_list_data_types`, `withings_list_weight`, `withings_list_body_composition`, `withings_list_measures`, `withings_list_measure_types`** | **`bridge:health:read`** | **health-mcp :9226 / withings-mcp :9227 — MCP `tools/call` through the CT121 PEP as a minted agent JWT** |

## Personal-health tools (health-mcp + withings-mcp)

Nine read-only 1:1 proxies over two internal MCP backends that live behind the
CT121-mcp-gateway PEP:

- **health-mcp** (`ste/health-mcp`, Google Health API v4 — Withings + Garmin + phone,
  aggregated via Health Connect): `health_list_weight/_sleep/_steps` (each optional
  ISO-8601 `start`/`end`), `health_list_datapoints` (`dataType` + optional window),
  `health_list_data_types` (no args).
- **withings-mcp** (`ste/withings-mcp`, Withings Public Health Data API): `withings_list_weight`,
  `withings_list_body_composition` (both optional `start`/`end`, ISO-8601 or unix seconds),
  `withings_list_measures` (`meastypes` + optional window), `withings_list_measure_types` (no args).

**Transport + identity — mirrors `SageCouncil.Mcp`, NOT the cervello REST-bearer path.**
The backends are MCP servers, reachable only as MCP `tools/call` through the agentgateway
PEP. Each call:

1. mints the bridge's **scoped agent identity** (`BRIDGE_HEALTH_AGENT`) as a short-lived
   RFC 7523 JWT via `Sinapsi.AgentJwt.AgentJwtMinter` (JWK loaded at call time from
   `AGENT_KEY_DIR`; nothing hardcoded), and
2. forwards the tool via `Sinapsi.Mcp.GatewayMcpClient` to `GATEWAY_URL`.

The gateway prefixes each backend tool with its alias, so the wire tool name IS `health_*`
/ `withings_*`. Fail-closed edges before any I/O: `HEALTH_EXPOSED=false` → `disabled`;
`BRIDGE_HEALTH_AGENT` unset → `not_configured`. A PEP DENY (401/403) → `unauthorized`;
gateway unreachable → `unreachable`. The backend JSON is passed through verbatim; audit
records the tool + outcome only.

### Config

`GATEWAY_URL`, `BRIDGE_HEALTH_AGENT`, `HEALTH_EXPOSED`, plus the `Sinapsi.AgentJwt` minter
env (`OIDC_ISSUER`, `OIDC_AUDIENCE_PROJECT_ID`, `AGENT_KEY_DIR`, `JWT_TTL_MIN`). See
`config.env.example`.

### Deploy follow-up (home-server) — REQUIRED before these tools work live

The bridge does **not** currently hold an agentgateway grant for `health_*` / `withings_*`.
Today those backends are reached only by `agent-brain-orchestrator` (jwt.sub
`373800316820258818`) via its blanket-allow CEL rule; **the bridge is a different, unlisted
identity** (`373800316820258818` is the brain, not the bridge — do NOT reuse it for the
claude.ai-facing bridge). Before flipping `HEALTH_EXPOSED` on in prod, home-server must:

1. **Provision a scoped agent identity** for the bridge (e.g. `agent-bridge-mcp`) in Zitadel,
   register its JWK agent-free from Infisical, and mount it read-only at `AGENT_KEY_DIR` on
   CT145-bridge-mcp; set `BRIDGE_HEALTH_AGENT` to that name.
2. **Grant it access through the PEP** — add a CEL rule to
   `services/agentgateway/config.yaml` allowing that jwt.sub to call the `health` + `withings`
   targets (target-pinned, least privilege, mirroring the existing per-agent rules), and add
   the matching OpenFGA tuples (`agent:bridge-mcp can_call tool:health.*` /
   `tool:withings.*`) for the enforce plane. Re-run `register-*-agentgateway.yml`.

Until (1)+(2) land, the tools return `not_configured` (no agent) or `unauthorized` (PEP
deny) — they fail closed and leak nothing.
