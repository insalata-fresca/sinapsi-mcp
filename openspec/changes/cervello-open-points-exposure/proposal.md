# Change: cervello open-points exposure to the operator's Claude app (S50 L3)

Spec-driven work-unit (sinapsi-speckit discipline). Discovery → proposal → specs →
design → tasks in this folder. This change makes the already-built open-points engine
(`OpenPointsService` + `PgOpenPointStore` + `TokenOpenPointsAuthGate`, E5/L1) reachable
from the operator's claude.ai web/mobile app — his ONLY enrichment UI — following the
M5 `career_search` exposure pattern (bridge tool → token-gated CT146 HTTP surface).

## Discovery (grounded facts)

- **M5 exposure pattern** (`Bridge.Mcp/Tools/BridgeCareerSearchTools.cs`): an
  `[McpServerToolType]` class exposes `[McpServerTool]` methods; each authorizes on the
  bridge side (`AuthService.Authorize(scope, bucket, limit)`), then calls a **token-gated
  HTTP surface on CT146** via a typed pooled `HttpClient` with `Authorization: Bearer
  <token>`. Config keys (`CAREER_SEARCH_URL` + `CAREER_SEARCH_TOKEN`) come from
  `BridgeConfig.FromEnvironment()`. The bridge is deployed on CT145-bridge-mcp and is the
  claude.ai connector edge.
- **Already built (E5/L1, on `main`):** `OpenPointsService.ListAsync/AnswerAsync` (full
  write-back: map-PR with `human://<answer-id>` basis, glossary upsert, voiceprint
  enroll/refine), `PgOpenPointStore` (`open_points` table on CT146), `IOpenPointsAuthGate`
  + `TokenOpenPointsAuthGate` (reads `CERVELLO_OPEN_POINTS_TOKEN`, **fails closed** on
  empty), redacted DTOs `OpenPointView`/`OpenPointAnswer`.
- **MISSING (this change):** (1) an HTTP transport on CT146 exposing list+answer, gated by
  `TokenOpenPointsAuthGate`; (2) DI registration of `OpenPointsService` + `VoiceprintEnrollment`
  (neither is registered today — only constructed in tests); (3) the two bridge MCP tools
  (`cervello_open_points_list` / `cervello_open_points_answer`) calling that surface,
  bridge-side scope-gated + `CERVELLO_EXPOSED` emergency-disable honored.
- **Deploy wiring** already renders `CERVELLO_OPEN_POINTS_TOKEN` into the enrichment
  `config.env` (agent-free from Infisical `/ct146/cervello/`). The enrichment host runs
  `Network=host`, health on `0.0.0.0:8147`. CT146 is egress deny-by-default; a bridge→CT146
  ingress path + a bridge-side token fetch are the home-server deploy deltas (separate repo).

## Why

At first real ingestion the pipeline escalates ambiguous attributions/corrections to the
`open_points` queue (escalate-only + dry-run are ON). The operator must be able to LIST
pending open-points and ANSWER them from Claude web/mobile so that answers apply the fact
(with a `human://` basis), update the glossary, and enroll/refine voiceprints — the
learning signal. Without exposure the queue is unreachable and ingestion stalls.

## What changes (this repo: ste/sinapsi-mcp)

1. **`Cervello.Enrichment` (engine):** add `OpenPointsService` + `VoiceprintEnrollment`
   to the LIVE DI composition (`AddCervelloEnrichment`) so the Host can resolve them.
   No logic change; the engine still references no MCP/HTTP package.
2. **`Cervello.Enrichment.Host` (CT146 transport):** add a token-gated HTTP surface on the
   existing health bind (a dedicated `/open-points` route group) that authorizes via
   `IOpenPointsAuthGate` (Bearer) then calls `OpenPointsService`. `GET /open-points` (list,
   optional `kind`/`recording` filters) + `POST /open-points/{id}/answer` (select/value/dismiss).
   401 on missing/invalid bearer; 404 unknown point; 200 with the applied result.
   `/healthz` unchanged.
3. **`Bridge.Mcp`:** add `BridgeOpenPointsTools` with `cervello_open_points_list` +
   `cervello_open_points_answer`. Bridge-side scope gate (`bridge:cervello:read` for list,
   `bridge:cervello:deposit` for answer — the write-back), a new typed `HttpClient`
   ("cervello-open-points", 10s), config keys `CERVELLO_OPEN_POINTS_URL` +
   `CERVELLO_OPEN_POINTS_TOKEN` + `CERVELLO_EXPOSED` (emergency-disable, ACCESS.md §7).
   New scopes advertised in RFC9728 metadata.

## Isolation invariants held (S50 / ACCESS.md)

- Surface A preconditions: exposed_workspaces allowlist + project-binding + cervello-scoped
  credential + deposit guard + access log — enforced at the bridge edge + the CT146 token gate.
- No cervello content on shared NATS/logs (§8): the Host binary opens no NATS; the CT146
  surface returns only redacted `OpenPointView`s (R10). Access is appended to the on-CT
  access log.
- Fail-closed: an unconfigured token gate refuses every call (stricter than M5's read-only
  `not_configured`); `CERVELLO_EXPOSED=false` severs the bridge tools immediately.
- No new denied egress opened; the answer write-back stays inside CT146's existing
  forgejo/DB allowlist (dry-run default unchanged).
