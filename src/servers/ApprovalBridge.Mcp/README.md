# ApprovalBridge.Mcp — the agent-facing REQUEST tool (E1.6)

The MCP tool a BLOCKED agent calls to surface a `requiresApproval` action to the operator, via the
`ApprovalBridge.Broker` (E1.3). Part of the Operator Approval Bridge
(home-server `docs/66-operator-approval-bridge.md`).

**Agents may request; they may never approve.** This server declares exactly one MCP tool —
`approval_bridge_request` — and has no code path to the broker's `/approve` or `/reject`
endpoints anywhere in its source. See "Why this tool cannot approve" below.

## What it does (in_scope, E1.6)

1. **`approval_bridge_request(action_id, params)`** — validates the call SHAPE (non-empty/bounded
   `action_id`, `params` is `{}` or a JSON object) before any network call, then submits the
   request to the broker's `POST /request` intake under this deployment's own configured
   `requester_identity` (never a caller-supplied identity).
2. The broker validates `action_id` against the E1.1 git-backed allowlist and `params` against its
   `param_schema`, deny-by-default (docs/66 §3.1, §8). This tool cleanly maps every broker
   rejection — unknown action, params failing schema, rate-limited — to a structured
   `{ok:false, error:"<Reason>"}` response.
3. On acceptance, returns `{ok:true, status:"pending", request_id, action_id, message}` — a
   **pending handle**, never an approval. The action has not run.

## Why this tool cannot approve

Two independent, structural barriers (docs/66 §8 T1 — the agent must be *structurally* unable to
self-approve):

- **No client method exists.** `ApprovalBridgeClient` — the only network seam this server has onto
  the broker — declares a single method, `RequestAsync`, which calls `POST /request` only. There
  is no `ApproveAsync` / `RejectAsync` anywhere in this server's source. A reviewer can confirm
  "this MCP cannot approve" by reading `ApprovalBridgeClient.cs` alone.
- **No tool is registered for it.** `ApprovalBridgeTools` (the `[McpServerToolType]` surface) has
  exactly one `[McpServerTool]` method. `Program.cs` registers only `ApprovalBridgeTools`.
- **Defense in depth (outside this repo's scope):** E1.5 (approve-channel authz, merged) scopes
  agent identities at the gateway to `approval_bridge_request` only — even a differently-built MCP
  could not reach `/approve` under an agent identity.

`test/ApprovalBridge.Mcp.Tests/RequestOnlyGuardTests.cs` and
`test/ApprovalBridge.Mcp.Tests/ToolSurfaceTests.cs` pin both barriers with reflection assertions
over the compiled types, so a future edit that tried to add an approve/reject tool would fail the
build's test gate.

## Configuration

| Env var | Required | Default | Purpose |
|---|:---:|---|---|
| `APPROVAL_BRIDGE_BROKER_URL` | yes | — | Base URL of the `ApprovalBridge.Broker` (E1.3). No broker host is baked into the image. |
| `APPROVAL_BRIDGE_REQUESTER_IDENTITY` | yes | — | This deployment's own agent identity (e.g. `agent:cervello-worker/ct139`), sent as `requester_identity` on every request. Never taken from the tool call. |
| `APPROVAL_BRIDGE_HTTP_TIMEOUT_MS` | no | `30000` | Bounds every call to the broker. Must be `1..600000`; out of range throws naming the var. |
| `APPROVAL_BRIDGE_MCP_HOST` / `APPROVAL_BRIDGE_MCP_PORT` | no | `0.0.0.0` / `9219` | MCP listen address (via `Sinapsi.Mcp`'s `MapSinapsiMcp`). |

## Out of scope

The approve/reject path (operator-only, Console E1.7), the executor (E1.4), go-live cutover
(docs/66 §10 — a later, operator-gated trust-boundary flip).

## Testing

```sh
dotnet test test/ApprovalBridge.Mcp.Tests
```
