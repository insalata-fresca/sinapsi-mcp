# Cervello.Watcher

The **WATCH → NORMALIZE** stages of the Cervello ingestion spine (scope-50
Phase 2c). A polling BackgroundService that detects new/changed recordings in a
Google Drive folder, downloads them custody-safely onto CT146-cervello, pairs
audio+transcript by basename, assigns a deterministic id, and registers exactly
one `recordings/manifest.yaml` entry per recording (SCHEMAS §8).

Like `Sinapsi.Indexer`'s isolated shape, this is a **no-NATS binary**: it
references neither `Sinapsi.Nats` nor any NATS client (invariant 3 / design D8).
The only thing that leaves the CT is an opaque `/healthz` heartbeat.

## Hard invariants

1. **Audio never in git.** `.m4a`/`.txt` bytes are staged ONLY under the CT
   staging dir (`Ingest/BlobStore.cs`), content-addressed by `audio_sha256`. The
   store THROWS if asked to write under a git working tree. Only references +
   checksums reach the manifest.
2. **No NATS in the binary.** The "ready for enrichment" signal is a LOCAL marker
   under the staging inbox (`Normalize/ReadyMarker.cs`), never a bus message.
3. **Drive access via the homelab `gdrive` MCP, not a Google credential (M6-refine).**
   `Drive/McpGdriveClient.cs` calls the existing `gdrive` MCP through the CT121
   agentgateway, authenticated as a scoped agentgateway machine identity
   (`agent-cervello-watcher`, minted by `Sinapsi.AgentJwt`). No Google service
   account, no GCP project, no interactive OAuth. CT121-mcp-gateway is already a
   fixed allowlisted LAN egress peer for this CT (`cervello_egress_lan_allow`).
4. **Everything idempotent.** Replay of `drive:<fileId>:<md5>` is a logged no-op;
   `rec:<recordingId>:<audio_sha256>` dedupes manifest entries; a re-run of a
   normalized recording leaves `manifest.yaml` byte-unchanged.

## Module layout

| Path | Role |
|------|------|
| `WatcherConfig.cs` | Fail-closed env config (bad value throws at startup). |
| `Domain/` | `PipelineState`, `DriveChange`, `Recording`, `ManifestEntry` (immutable). |
| `Drive/IDriveClient.cs` | The test seam (get-start-token, list-changes, get-metadata, download-media). |
| `Drive/McpGdriveClient.cs` | Real `IDriveClient` over the gdrive MCP via the agentgateway (M6-refine — `GatewayMcpClient` + `AgentJwtMinter`, folder-id resolution, list/diff cursor emulation, chunked download). |
| `Drive/DriveMediaException.cs` | Transient/terminal classification for a Drive-media fetch failure (replaces catching `Google.GoogleApiException`). |
| `State/` | `IStateStore` + `InMemoryStateStore` (tests) + `PostgresStateStore` (D4). |
| `Ingest/IdempotencyLedger.cs` | `drive:<fileId>:<md5>` ledger — replay no-op, modified supersede. |
| `Ingest/BlobStore.cs` | Custody-safe staging; guard rejects repo paths. |
| `Ingest/Downloader.cs` | Streams via `IDriveClient`; transient→retryable, terminal→reason. |
| `Normalize/Pairer.cs` | Basename pairing, arrival-order tolerant. |
| `Normalize/Normalizer.cs` | Deterministic id + `recorded_at` (D5). |
| `Normalize/YamlManifestStore.cs` | Hand-rolled, byte-stable, idempotent §8 append. |
| `Normalize/ReadyMarker.cs` | Local ready marker (no NATS). |
| `WatchWorker.cs` | The poll loop; cursor advances only after a batch fully processes. |
| `Program.cs` | Host builder, DI, opaque `/healthz`. |

## One-time setup (operator) — M6-refine, supersedes the Google-SA precondition

The watcher uses the **existing homelab `gdrive` MCP** (already authenticated to
Drive) via the CT121 agentgateway — **no Google service account, no GCP project,
no interactive OAuth**:

1. Provision a scoped agentgateway machine identity `agent-cervello-watcher`
   (Zitadel machine user + key, mirroring `agent-council-gemini`/`-chatgpt`) and
   land its JWK agent-free at `<AGENT_KEY_DIR>/agent-cervello-watcher.json`.
2. Confirm CT121-mcp-gateway is reachable from CT146-cervello — it already is
   (`cervello_egress_lan_allow`, ports 443/8443); no egress change needed.
3. Store the Postgres password as **Infisical `/ct146/cervello/CERVELLO_DB_PASSWORD`**.
4. Optionally set `CERVELLO_WATCHER_FOLDER_ID` directly to skip the
   `gdrive_search_files` folder-name resolution the watcher otherwise does once
   at startup.

The WATCH/NORMALIZE core is fully exercised against a `FakeDriveClient` in the
test suite; the gdrive-MCP seam itself is exercised end-to-end against a
scripted gateway in `McpGdriveClientTests` (list/diff, chunked download,
folder resolution, transient/terminal error classification) — no live Drive
dependency remains for the deploy gate (Q1 dissolved; see `ste/cervello`
`openspec/changes/cervello-watcher/discovery.md` Q1-refine).

## Config

See `config.env.example`. All knobs default to neutral local placeholders;
numeric knobs are fail-closed.

## Build / test

```
dotnet build src/servers/Cervello.Watcher/Cervello.Watcher.csproj
dotnet test  test/Cervello.Watcher.Tests/Cervello.Watcher.Tests.csproj
```

Image (from the repo root):

```
podman build --network=host -f src/servers/Cervello.Watcher/Containerfile -t cervello-watcher .
```
