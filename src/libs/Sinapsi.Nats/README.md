# Sinapsi.Nats

A personal-lab .NET library: small base classes for building
[NATS](https://nats.io) JetStream daemons and publishing
[CloudEvents](https://cloudevents.io). Offered as-is.

It bundles the connection + loop boilerplate that a JetStream consumer or a
KV-watch daemon otherwise hand-rolls each time, and a CloudEvents v1.0 publisher
so producers and consumers agree on the envelope shape.

## What's inside

- **`NatsConnectionOptions`** — connection settings (NKey-seed auth + optional
  pinned-CA TLS) read entirely from the environment via
  `NatsConnectionOptions.FromEnvironment()`, turned into a `NatsOpts` by
  `BuildNatsOpts()`. An opt-in `NATS_TLS_DISABLE` knob connects to a plaintext
  (no-TLS) bus while keeping nkey auth — handy for an ephemeral local/test bus.
- **`JetStreamWorker`** — a `BackgroundService` base that connects, creates (or
  updates) a durable consumer for a subject filter, and runs a self-healing
  fetch/ack long-poll loop. Subclasses supply the stream / durable / filter and
  implement `ProcessAsync`. A `Ready` flag and an `EventsProcessed` counter feed
  a health endpoint.
- **`KvWatchWorker`** — a sibling base that mirrors a single JetStream KV key:
  seeds the current value, then watches it push-fresh and calls `OnValueAsync`
  on every change. Auto-reconnecting, fail-open.
- **`NatsEventPublisher`** — publishes a `JsonObject` payload as a CloudEvents
  v1.0 envelope on a NATS subject, with a stable `id`/`time`/`source` and a
  configurable reverse-DNS `type` prefix.

## Configuration

Everything is environment-driven, with safe defaults for a local plaintext bus:

| Env var | Default | Meaning |
|---|---|---|
| `NATS_URL` | `nats://127.0.0.1:4222` | Server URL. |
| `NATS_NKEY_SEED_PATH` | `nats.seed` | File holding the NKey seed (`S...`). Used only if present. |
| `NATS_NKEY` | *(unset)* | Public NKey (`U...`). NATS.Net requires both the public key and the seed for nkey auth. |
| `NATS_TLS_CA_FILE` | *(unset)* | Pinned CA. Unset → system trust store. |
| `NATS_TLS_DISABLE` | `false` | Truthy (`1` / `true`) → connect to a PLAINTEXT (no-TLS) bus; nkey auth retained. |
| `NATS_CLIENT_NAME` | `sinapsi-nats` | Client name reported to the server. |
| `CLOUDEVENTS_TYPE_PREFIX` | `com.example.` | Reverse-DNS prefix for the CloudEvents `type` attribute. |

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

// register
builder.Services.AddSingleton(NatsConnectionOptions.FromEnvironment() with { ClientName = "my-service" });
builder.Services.AddHostedService<MyWorker>();
```

```csharp
// Publish a CloudEvent.
await using var pub = await NatsEventPublisher.ConnectAsync(opts, source: "my-service://node-1/");
await pub.PublishAsync("my.subject.created", new JsonObject { ["id"] = 42 }, subjectAttr: "42");
```

## Build / test

Targets **.NET 8**.

```sh
dotnet build  -c Release src/libs/Sinapsi.Nats/Sinapsi.Nats.csproj
dotnet test   -c Release test/Sinapsi.Nats.Tests/Sinapsi.Nats.Tests.csproj
dotnet pack   -c Release src/libs/Sinapsi.Nats/Sinapsi.Nats.csproj
```

The only runtime dependencies are `NATS.Net` and the
`Microsoft.Extensions.Hosting`/`Logging` abstractions, all from nuget.org.
