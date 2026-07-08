# Cervello.Enrichment.Host — the enrichment engine's deploy-slice host

This is the **CT146-cervello background-worker binary** that hosts the merged
`Cervello.Enrichment` engine (`AddCervelloEnrichment`) and **drains** the recordings the M6
`Cervello.Watcher` has driven to `normalized`. It is the host L2 deploys; the engine itself is a DI
library with no `main` — this binary is what runs the pipeline.

Like the engine + the Watcher (invariant 3 / D8) it opens **NO NATS connection** — it references
neither `Sinapsi.Nats` nor any NATS client. The only thing that leaves the CT is an **opaque health
heartbeat**; recording data stays on-CT (custody).

## The drain loop

A `BackgroundService` (`DrainWorker`) polls on an interval:

1. **Lease** a bounded batch of recordings in `normalized` from `INormalizedWorkQueue`.
2. For each, run the engine's **`IngestStage`** — it atomically **claims** the SCHEMAS §8
   idempotency key `rec:<id>:<audio-sha256>` via `IEnrichmentLedger` (a replay of a seen key is a
   logged no-op) and advances `normalized → enriched` under the **escalate-only** phase gate.
3. **Advance** the shared state row so the recording is no longer re-leased.
4. A per-item throw maps the recording to **`failed_retryable`** (SCHEMAS §5, retried under the same
   key next cycle) without aborting the batch.

## The `normalized` → host handoff (drain contract)

The Watcher persists `watcher_recording` rows carrying `state = 'normalized'` (the §5 wire name, via
`Cervello.Watcher.Domain.PipelineStateWire`). `INormalizedWorkQueue`'s live adapter
(`PgNormalizedWorkQueue`) is a **read-only, additive view over exactly those rows** — plus the one
`UPDATE … SET state` advance. **No watcher-side change is needed**: the Watcher already writes the
`normalized` signal this drains, and the two share the row (E4 enum reconciliation). The fake
(`InMemoryNormalizedWorkQueue`) drives the loop offline in tests.

## Escalate-only + dry-run by default

The engine's `EnrichmentConfig` defaults enforce the gate: `CERVELLO_GRADED_AUTO_APPLY=false`
(escalate-only — every band → an open-point, no auto-write) and `CERVELLO_MAP_PR_DRY_RUN=true` (no
real map-PR). The host flips no auto-apply; the drain advances only to `enriched`.

## Scope boundary (E-HOST)

This host owns the drain **mechanism** (poll → claim → run the pipeline entry → advance → idempotent
replay → failure mapping → health). Threading **all eight stages** end-to-end
(diarize/attribute/correct/enrich/bundle/apply) into a single recording's run needs per-stage inputs
derived from real audio + a stage-to-stage data-flow the L1 engine library deliberately did not ship
(no uniform stage interface, no orchestrator). **That full inter-stage orchestrator is a follow-up
mission, not E-HOST.**

## Config

See `config.env.example`. Two blocks: the ENGINE config (`EnrichmentConfig` — live-vs-fake, phase
gate, endpoints, DSN) and this host's own drain-loop knobs (`HostConfig` — poll interval, batch
size, health bind). All fail-closed. Secrets (the `agent-cervello-enrichment` JWK, DB password,
open-points token) arrive agent-free from Infisical `/ct146/cervello/` at deploy — never committed.

## Health

`GET /healthz` — `200 {"status":"ok"}` once the worker is up, `503` while starting. Opaque body
(no recording data). Default bind `0.0.0.0:8147` (one above the Watcher's 8146; the two cervello
workers co-reside on CT146).
