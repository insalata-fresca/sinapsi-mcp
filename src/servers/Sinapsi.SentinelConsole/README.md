# Sinapsi.SentinelConsole — the Authorization Console

The **inspection surface** of the agent authorization plane (home-server
`docs/61`, Scope 50): one screen that shows what's happening across all three
layers, live, so understanding the posture never again requires an agent session
grepping across repos and hosts.

It is the read/inspection half of **Sinapsi Sentinel** — a deliberate charter
expansion of the bus-inspection reflex from "anomaly hints" to "the authz
inspection + control plane." This service is **read-only**: it subscribes,
projects, and renders. The operator-driven action layer is a separate, governed
step (Scope 50 phase 9).

## What it does

- Subscribes (core NATS, read-only) to `homelab.security.>` — the authorization
  decision family: `authz.q2.*` (Q2 in-MCP command authorizer), `ask-gate.*` /
  `deny-floor.*` / `tier4.*` / `credential-guard.*` / `scope_gate.*` (Q3 harness).
- Normalizes each event into an `AuthzDecision` and maintains a bounded in-memory
  read-model: a **posture grid** (per tool × layer: latest verdict + running
  allow/approval/deny counts) and a **live feed** (recent decisions, newest first).
- Serves a single buildless page (`wwwroot/index.html`): the posture grid on top,
  the live decision feed below (Server-Sent Events), each row expandable to its
  **cross-layer chain** (all decisions sharing a `correlation_id`).

## Tool surface (HTTP)

| Route | Returns |
|---|---|
| `GET /` | the Console page |
| `GET /api/posture` | posture grid rows (tool × layer + counts + last verdict) |
| `GET /api/recent?n=` | recent decisions, newest first (feed backfill) |
| `GET /api/chain/{id}` | all decisions sharing a `correlation_id` (the per-request chain) |
| `GET /api/stats` | `{ total, ingested, connected, clients }` |
| `GET /events` | Server-Sent-Events live decision stream |
| `GET /healthz` | 200 when bus-connected, 503 (degraded) otherwise |

## Configuration

| Env | Default | Meaning |
|---|---|---|
| `SENTINEL_CONSOLE_PORT` | `8140` | listen port |
| `SENTINEL_CONSOLE_BUFFER` | `2000` | live-feed ring capacity (bounds memory) |
| `SENTINEL_CONSOLE_DEMO` | — | `1` ⇒ seed synthetic decisions so the page renders populated with NO live bus (dev/first-look only; off by default) |
| `NATS_*` | — | the shared connection env (URL / TLS / NKey seed). Use a **read-only** identity — subscribe-only on `homelab.security.>`; this service never publishes. |

## Correlation — the per-request chain

The chain view joins decisions by `correlation_id`. Today two of three layers
emit (Q2 live via `sinapsi-mcp` `AuthzDecisionPublisher`; Q3 via the harness),
and a shared trace id is not yet threaded harness→gateway→MCP, so a chain shows
whichever layers recorded that id. Threading one id across all three (Scope 50
phase 6, `docs/61 §8`) lights up the full Q1→Q2→Q3 path per request.

## Try it locally

```bash
SENTINEL_CONSOLE_DEMO=1 SENTINEL_CONSOLE_PORT=8140 \
  dotnet run --project src/servers/Sinapsi.SentinelConsole
# open http://127.0.0.1:8140/  → populated posture grid + live feed
```
