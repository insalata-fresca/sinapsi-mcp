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

### Deploy-visibility lane (M12) — "did my merge actually deploy?"

- A second, separate subscriber (`DeployBusSubscriber`) additionally listens
  (core NATS, read-only) to `homelab.release.>` (the `build-push-release` CI
  action's `homelab.release.<svc>.published`, home-server `docs/23` §1.4) and
  `homelab.deploy.>` (the per-host deploy-controller's
  `homelab.deploy.<ctid>.<svc>.applied` / `.failed`, `docs/23` §2 /
  `patterns/deploy-service.md`).
- Normalizes each event into a `DeployEvent` and maintains a second bounded
  in-memory read-model (`DeployModel`): **per-service state** (last released
  version/digest, last applied version/digest/ctid/result) and a **recent-events
  feed** (newest first).
- The page's **Deploys** section (above the posture grid) shows the per-service
  table plus the recent-events feed, polled every 4s.

> **Follow-up required before this is live (not done in this PR):** the
> Console's NATS identity is currently scoped subscribe-only on
> `homelab.security.>` (home-server `nats_server_users`). `DeployBusSubscriber`
> will connect but receive nothing until that identity's subscribe permissions
> are widened to also include `homelab.release.>` + `homelab.deploy.>` — a
> home-server Ansible change (`playbooks/roles/nats_server/`), tracked
> separately. It fails safe (silently idle), never open.

## Tool surface (HTTP)

| Route | Returns |
|---|---|
| `GET /` | the Console page |
| `GET /api/posture` | posture grid rows (tool × layer + counts + last verdict) |
| `GET /api/recent?n=` | recent decisions, newest first (feed backfill) |
| `GET /api/chain/{id}` | all decisions sharing a `correlation_id` (the per-request chain) |
| `GET /api/deploys?n=` | recent `DeployEvent`s (release + applied/failed), newest first |
| `GET /api/deploy-state` | per-service latest state: last released version/digest + last applied version/digest/ctid/result |
| `GET /api/stats` | `{ total, ingested, connected, clients, deploysTotal, deploysIngested, deployBusConnected }` |
| `GET /events` | Server-Sent-Events live decision stream (authz only — the deploy lane is poll-based, see below) |
| `GET /healthz` | 200 when the security bus is connected, 503 (degraded) otherwise |

## Configuration

| Env | Default | Meaning |
|---|---|---|
| `SENTINEL_CONSOLE_PORT` | `8140` | listen port |
| `SENTINEL_CONSOLE_BUFFER` | `2000` | authz live-feed ring capacity (bounds memory) |
| `SENTINEL_CONSOLE_DEPLOY_BUFFER` | `500` | deploy-events ring capacity (bounds memory) |
| `SENTINEL_CONSOLE_DEMO` | — | `1` ⇒ seed synthetic decisions so the page renders populated with NO live bus (dev/first-look only; off by default) |
| `NATS_*` | — | the shared connection env (URL / TLS / NKey seed). Use a **read-only** identity — subscribe-only on `homelab.security.>` + (once the follow-up below lands) `homelab.release.>` / `homelab.deploy.>`; this service never publishes. |

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
