# Sinapsi.Nats

A small, env-driven .NET library for building [NATS](https://nats.io) JetStream
daemons and publishing [CloudEvents](https://cloudevents.io) v1.0 envelopes. It
bundles the connection (NKey-seed auth + optional pinned-CA TLS), the durable
fetch/ack loop, a KV-watch base, and a CloudEvents publisher — the boilerplate a
consumer or producer otherwise hand-rolls each time. A personal research-lab
library, offered as-is. All runtime dependencies are from nuget.org.

## Contents

- [Overview](#overview)
- [Public API reference](#public-api-reference)
- [Configuration](#configuration)
- [Usage](#usage)
- [Security notes](#security-notes)
- [Error contract](#error-contract)
- [Testing](#testing)

## Overview

The library turns a process's environment into a validated NATS client and gives
you three ready-made building blocks:

- **`NatsConnectionOptions`** — a record of connection settings read from the
  environment via `FromEnvironment()` and turned into a `NatsOpts` by
  `BuildNatsOpts()`. Fails closed on a malformed URL / NKey / connect timeout.
- **`JetStreamWorker`** — a `BackgroundService` base that connects, creates (or
  updates) a durable consumer for a subject filter, and runs a self-healing
  fetch/ack long-poll loop. Exposes a `Ready` flag and an `EventsProcessed`
  counter for a health endpoint.
- **`KvWatchWorker`** — a sibling base that mirrors a single JetStream KV key:
  seeds the current value, then watches it push-fresh and calls `OnValueAsync`
  on every change. Auto-reconnecting, fail-open.
- **`NatsEventPublisher`** — publishes a `JsonObject` payload as a CloudEvents
  v1.0 envelope on a NATS subject, with a stable `id`/`time`/`source` and a
  configurable reverse-DNS `type` prefix.

## Public API reference

### `NatsConnectionOptions` (record)

| Member | Purpose | Inputs / errors |
|---|---|---|
| `FromEnvironment()` | Build options from the process environment with neutral defaults. | Throws `InvalidOperationException` (naming the env var) if `NATS_URL` / `NATS_NKEY` is malformed or `NATS_CONNECT_TIMEOUT_MS` is non-numeric / `<= 0` / above the ceiling. |
| `Validate()` | Fail-closed check of URL, public NKey, and connect-timeout bound. | Throws `InvalidOperationException` naming the offending value. Called by `FromEnvironment()` and `BuildNatsOpts()`. |
| `BuildNatsOpts()` | Turn the options into a NATS.Net `NatsOpts` (NKey+seed auth, TLS, bounded connect timeout). | Calls `Validate()` first, so a directly-constructed bad record still fails closed. |
| `ConnectTimeoutMs` | Bound (ms) on the TCP+auth connect. | Default `10000`; ceiling `120000`. |

### `NatsEventPublisher`

| Member | Purpose | Inputs / errors |
|---|---|---|
| `ConnectAsync(opts, source, ct)` | Connect and return a ready publisher. | `source` required + control-char free (`ArgumentException`); `opts` non-null (`ArgumentNullException`); a connect failure is re-thrown sanitized (`InvalidOperationException`, no secret in the message). |
| `PublishAsync(subject, data, subjectAttr, ct)` | Publish `data` on `subject` wrapped in a CloudEvent. | `subject` required, length-capped, control-char / empty-token free (`ArgumentException`); `data` non-null; a publish failure is re-thrown sanitized. |
| `DefaultTypePrefix` | Neutral default reverse-DNS `type` prefix (`com.example.`). | Overridden by `CLOUDEVENTS_TYPE_PREFIX`. |

### `JetStreamWorker` / `KvWatchWorker` (abstract `BackgroundService` bases)

Subclass and supply the stream/durable/filter (or bucket/key) and implement
`ProcessAsync` (or `OnValueAsync`). `Ready` reports readiness for a health probe;
`JetStreamWorker.EventsProcessed` counts projected events;
`JetStreamWorker.DeliverPolicy` is overridable (default `DeliverAll`).

## Configuration

Everything is environment-driven, with neutral defaults for a local plaintext bus.
A **malformed** value fails closed (an error naming the var); an **unset** value
falls back to the default.

| Env var | Required | Default | Purpose |
|---|---|---|---|
| `NATS_URL` | No (defaulted) | `nats://127.0.0.1:4222` | Server URL. Must use scheme `nats:// tls:// ws:// wss://`; control/whitespace rejected. |
| `NATS_NKEY_SEED_PATH` | No | `nats.seed` | File holding the NKey seed (`S...`). Used only if present. |
| `NATS_NKEY` | No (nkey auth opt-in) | *(unset)* | Public NKey (`U...`). NATS.Net requires both the public key and the seed. Malformed non-empty value rejected. |
| `NATS_TLS_CA_FILE` | No | *(unset)* | Pinned CA. Unset → system trust store. |
| `NATS_TLS_DISABLE` | No | `false` | Truthy (`1` / `true`) → connect to a PLAINTEXT (no-TLS) bus; nkey auth retained. |
| `NATS_CLIENT_NAME` | No | `sinapsi-nats` | Client name reported to the server. |
| `NATS_CONNECT_TIMEOUT_MS` | No | `10000` | Bound (ms) on connect. Rejected if non-numeric, `<= 0`, or `> 120000`. |
| `CLOUDEVENTS_TYPE_PREFIX` | No | `com.example.` | Reverse-DNS prefix for the CloudEvents `type` attribute. |

## Usage

```csharp
using Sinapsi.Nats;

// A durable consumer of a subject filter.
public sealed class MyWorker : JetStreamWorker
{
    public MyWorker(NatsConnectionOptions opts, ILogger<MyWorker> log) : base(opts, log) { }

    protected override string StreamName    => "MY_STREAM";
    protected override string DurableName   => "my-worker";
    protected override string FilterSubject => "my.subject.>";

    protected override ValueTask ProcessAsync(string subject, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        // project the event…
        return ValueTask.CompletedTask;
    }
}

// register (FromEnvironment fails closed on bad config at startup)
builder.Services.AddSingleton(NatsConnectionOptions.FromEnvironment() with { ClientName = "my-service" });
builder.Services.AddHostedService<MyWorker>();
```

```csharp
// Publish a CloudEvent.
await using var pub = await NatsEventPublisher.ConnectAsync(opts, source: "my-service://node-1/");
await pub.PublishAsync("my.subject.created", new JsonObject { ["id"] = 42 }, subjectAttr: "42");
```

## Security notes

- **NKey seeds never leave the process in an error.** The seed is the private half
  of the identity; a connect/publish failure is routed through a central
  sanitizer that redacts seed / NKey / private-key / credential material (see the
  error contract) so it can never appear in a thrown message or a log line.
- **Fail closed on bad config.** A malformed URL / NKey or an out-of-range connect
  timeout throws at bind time (naming the var) rather than silently connecting to
  an unintended endpoint or hanging on an unbounded connect.
- **TLS by default.** `TlsMode.Auto` (system or pinned CA) is the default; a
  plaintext bus is *opt-in* via `NATS_TLS_DISABLE`, which nkey auth still guards.
- **Neutral defaults only.** No environment-specific host, domain, or brand is
  baked into any default.

## Error contract

Any message this library surfaces to a caller — in a thrown exception or a log
line built from it — is passed through a central `Sanitize()` that:

- redacts a NATS **NKey seed** (`S...`) and a public **NKey** (`U/A/O/N/C...`);
- redacts a PEM **private-key block** (RSA / EC / PKCS#8 / OpenSSH);
- strips **userinfo credentials** embedded in a connection URL
  (`nats://user:password@host` → `nats://[redacted]@host`);
- redacts `password | secret | token | api-key | bearer | authorization | seed |
  nkey` **assignments** to end-of-line (key name kept for diagnosability);
- **length-caps** the whole message (2000 chars) so a pathological dump cannot
  blow up a response or a log line.

The original (unsanitized) exception is retained as `InnerException` for local
diagnostics only; only the sanitized surface message is safe to log or return.

## Testing

Targets **.NET 8**.

```sh
dotnet build  -c Release src/libs/Sinapsi.Nats/Sinapsi.Nats.csproj
dotnet test   -c Release test/Sinapsi.Nats.Tests/Sinapsi.Nats.Tests.csproj
dotnet pack   -c Release src/libs/Sinapsi.Nats/Sinapsi.Nats.csproj
```

The test suite proves the hardening paths actually fire:

- **Fail-closed config matrix** (`NatsConfigFailClosedTests`) — a missing/malformed
  URL or NKey and an out-of-range / non-numeric connect timeout throw naming the
  var; neutral defaults still succeed; `BuildNatsOpts()` on a directly-constructed
  bad record still fails closed.
- **Input-validation matrix** (`NatsInputValidationTests`) — subject / source /
  URL / NKey validators reject empty, over-long, control-char, NUL (`\0`), and
  empty-token inputs; `ConnectAsync` / `PublishAsync` reject malformed input with
  `ArgumentException` before any network I/O.
- **Error-sanitization contract** (`NatsErrorsTests`) — a seed / NKey /
  private-key block / URL credential / secret assignment embedded in an error is
  `[redacted]` in the surfaced message, and the message is length-capped.
- **Worker contract** (`WorkerContractTests`) — the protected member surface and
  readiness/counter behaviour of the two `BackgroundService` bases.
