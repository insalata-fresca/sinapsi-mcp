# Sinapsi.Nats — connection contract (v0.4.2)

> Authored from the library source (`NatsConnectionOptions.cs`).

## Package identity

- **NuGet ID**: `Sinapsi.Nats`
- **Version**: `0.4.2`
- **Target framework**: `net8.0`

## Dependencies

- `PackageReference NATS.Net 2.8.0` — provides `NatsConnection`, `NatsOpts`, `NatsAuthOpts`, `NatsTlsOpts`, `TlsMode`, and the JetStream / KV contexts.

## Connection options — `NatsConnectionOptions`

`NatsConnectionOptions.FromEnvironment()` reads the env contract below and
`BuildNatsOpts()` turns it into a `NatsOpts`. All workers
(`JetStreamWorker`, `KvWatchWorker`, `NatsEventPublisher`) connect through
`BuildNatsOpts()`, so the knobs apply uniformly.

| Env var | Property | Default | Meaning |
|---|---|---|---|
| `NATS_URL` | `Url` | `nats://127.0.0.1:4222` | Server URL. |
| `NATS_NKEY_SEED_PATH` | `NKeySeedPath` | `nats.seed` | File holding the NKey seed (`S...`). Read only if the file exists. |
| `NATS_NKEY` | `NKeyPublic` | *(unset)* | Public NKey (`U...`). NATS.Net requires BOTH the public key and the seed for nkey auth. Non-secret. |
| `NATS_TLS_CA_FILE` | `TlsCaFile` | *(unset)* | Pinned CA. Unset → system trust store (`TlsMode.Auto`, no pinned CA). |
| `NATS_TLS_DISABLE` | `TlsDisable` | `false` | **Opt-in.** Truthy (`1` / `true`, case-insensitive) → connect to a PLAINTEXT (no-TLS) bus. |
| `NATS_CLIENT_NAME` | `ClientName` | `sinapsi-nats` | Client name reported to the server. |

## TLS behaviour — `BuildNatsOpts()`

Authentication is NKey public + seed (`NatsAuthOpts { NKey, Seed }`); the seed is
read from `NKeySeedPath` when that file exists. TLS is selected as follows:

1. **`NATS_TLS_DISABLE` truthy** → `NatsTlsOpts { Mode = TlsMode.Disable }` — **no `CaFile` set at all**. Plaintext connection; nkey auth is retained (nkey works over plaintext).
2. **else `TlsCaFile` empty/unset** → `NatsTlsOpts { Mode = TlsMode.Auto }` (system trust store, no pinned CA).
3. **else** → `NatsTlsOpts { CaFile = TlsCaFile, Mode = TlsMode.Auto }` — TLS verified against the pinned CA.

### Why `NATS_TLS_DISABLE` exists

When `TlsCaFile` is set, the combination `{ CaFile set, Mode = TlsMode.Auto }`
**forces a TLS handshake** in NATS.Net 2.8.0. To run a candidate service against an
ephemeral, no-TLS bus (e.g. an integration/parity test) there must be an env-level
way to drop both the CA and TLS. `NATS_TLS_DISABLE=1` is that knob.

## CloudEvents type prefix — `NatsEventPublisher`

The CloudEvents `type` attribute is `{prefix}{subject}`, where `prefix` comes from
the `CLOUDEVENTS_TYPE_PREFIX` env var (default `com.example.`). Set it to a
reverse-DNS namespace you own so emitted events carry your namespace.

## Stability + version semantics

- **v0.4.x** is API-stable. `NATS_TLS_DISABLE` is additive and opt-in: when unset,
  `BuildNatsOpts()` behaves as it did before the knob existed (TLS on when a CA is
  configured).
- The default connection path (pinned-CA TLS + nkey, when configured) is the
  production norm; `NATS_TLS_DISABLE` is intended for ephemeral/test buses, not for
  production services.
