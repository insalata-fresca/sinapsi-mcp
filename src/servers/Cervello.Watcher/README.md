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
3. **All Drive egress via the proxy.** `Drive/ProxyHttpClientFactory.cs` sets a
   `WebProxy` (default `http://127.0.0.1:13130`) on every Drive HTTP client —
   nftables drops direct Google egress.
4. **Everything idempotent.** Replay of `drive:<fileId>:<md5>` is a logged no-op;
   `rec:<recordingId>:<audio_sha256>` dedupes manifest entries; a re-run of a
   normalized recording leaves `manifest.yaml` byte-unchanged.

## Module layout

| Path | Role |
|------|------|
| `WatcherConfig.cs` | Fail-closed env config (bad value throws at startup). |
| `Domain/` | `PipelineState`, `DriveChange`, `Recording`, `ManifestEntry` (immutable). |
| `Drive/IDriveClient.cs` | The test seam (get-start-token, list-changes, get-metadata, download-media). |
| `Drive/ProxyHttpClientFactory.cs` | Proxy on every Drive client (D2, unit-assertable). |
| `Drive/DriveClientFactory.cs` | Read-only ServiceAccountCredential (DriveReadonly) + proxy + clamped timeout (D1). |
| `Drive/GoogleDriveClient.cs` | Real `IDriveClient` (live behaviour gated on the SA — Q1). |
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

## One-time setup (operator, Q1)

The watcher needs a **read-only, folder-limited Google service account** —
provisioned once by the operator (interactive OAuth is forbidden by design):

1. In Google Cloud, create a **service account** and a **JSON key**. Enable the
   Drive API for its project.
2. In Google Drive, **share the `cervello/recordings` folder** with the service
   account's email, **Viewer** access only. The SA sees nothing outside that share.
3. Store the JSON key in **Infisical `/ct146/cervello/`** (e.g.
   `CERVELLO_WATCHER_SA_KEY`). At deploy it is materialised to a `0600` file and
   its path is passed as `CERVELLO_WATCHER_SA_KEY_PATH`.
4. Store the Postgres password as **Infisical `/ct146/cervello/CERVELLO_DB_PASSWORD`**.

Until the SA is provisioned, the WATCH/NORMALIZE core is fully exercised against
a `FakeDriveClient` in the test suite (the live Drive-drop acceptance test is
gated on this setup — tasks.md 10.2).

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
