using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;

namespace Sinapsi.Nats;

/// <summary>
/// Base for a daemon that mirrors a single JetStream KV key locally: connects
/// (NKey+TLS), seeds the current value, then watches the key push-fresh and
/// invokes <see cref="OnValueAsync"/> on every change. Auto-reconnecting,
/// fail-open. Sibling of <see cref="JetStreamWorker"/> for the KV-watch pattern.
/// </summary>
public abstract class KvWatchWorker : BackgroundService
{
    private readonly NatsHomelabOptions _opts;
    protected readonly ILogger Log;

    public bool Ready { get; private set; }

    protected KvWatchWorker(NatsHomelabOptions opts, ILogger log)
    {
        _opts = opts;
        Log = log;
    }

    protected abstract string Bucket { get; }
    protected abstract string Key { get; }

    /// <summary>Handle a new value for the watched key.</summary>
    protected abstract ValueTask OnValueAsync(byte[] value, CancellationToken ct);

    /// <summary>Called when the key is absent at seed time (default: no-op).</summary>
    protected virtual ValueTask OnAbsentAsync(CancellationToken ct) => ValueTask.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await using var nc = new NatsConnection(_opts.BuildNatsOpts());
        var kvCtx = new NatsKVContext(new NatsJSContext(nc));
        var store = await kvCtx.GetStoreAsync(Bucket, ct);
        Log.LogInformation("watching {bucket}/{key}", Bucket, Key);

        // Seed with the current value (or signal absence) before watching.
        try
        {
            var entry = await store.GetEntryAsync<byte[]>(Key, cancellationToken: ct);
            if (entry.Value is { } v) await OnValueAsync(v, ct);
        }
        catch (NatsKVKeyNotFoundException)
        {
            await OnAbsentAsync(ct);
        }
        catch (Exception e)
        {
            Log.LogWarning(e, "initial KV read failed for {key}", Key);
        }

        Ready = true;

        // Watch updates only (seed already handled); auto-retry the loop.
        var opts = new NatsKVWatchOpts { UpdatesOnly = true, IgnoreDeletes = true };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var entry in store.WatchAsync<byte[]>(Key, opts: opts, cancellationToken: ct))
                {
                    if (entry.Operation == NatsKVOperation.Put && entry.Value is { } v)
                        await OnValueAsync(v, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Log.LogWarning(e, "watch loop error — retrying in 5s");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }
}
