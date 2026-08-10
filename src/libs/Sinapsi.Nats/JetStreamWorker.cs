using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Sinapsi.Nats;

/// <summary>
/// Base for a hosted daemon that durably consumes a JetStream subject filter
/// and processes each event. Handles connect (NKey+TLS), durable-consumer
/// create-or-update, the consume/ack loop, and a readiness flag for health.
/// Subclasses implement the projection in <see cref="ProcessAsync"/>.
/// </summary>
public abstract class JetStreamWorker : BackgroundService
{
    private readonly NatsConnectionOptions _opts;
    protected readonly ILogger Log;

    /// <summary>True once connected and the durable consumer is bound.</summary>
    public bool Ready { get; private set; }
    public long EventsProcessed { get; private set; }

    protected JetStreamWorker(NatsConnectionOptions opts, ILogger log)
    {
        _opts = opts;
        Log = log;
    }

    protected abstract string StreamName { get; }
    protected abstract string DurableName { get; }
    protected abstract string FilterSubject { get; }

    /// <summary>The subject filter(s) the durable consumer binds. Defaults to the single
    /// <see cref="FilterSubject"/> (byte-unchanged behaviour for every existing subclass). A
    /// subclass that consumes SEVERAL disjoint subtrees under one durable (e.g. the delivery
    /// evaluator over <c>homelab.git.&gt;</c> + <c>homelab.release.&gt;</c> + <c>homelab.deploy.&gt;</c>)
    /// overrides this to return &gt;1 subject; the consumer is then created with
    /// <c>FilterSubjects</c> (plural) instead of the single <c>FilterSubject</c> (JetStream
    /// forbids setting both). An empty result is treated as "no override" and falls back to
    /// <see cref="FilterSubject"/>.</summary>
    protected virtual IReadOnlyList<string> FilterSubjects => new[] { FilterSubject };

    /// <summary>JetStream delivery policy for the durable consumer. Defaults to
    /// <c>DeliverAll</c> (replay the full retained history — correct for last-write-wins
    /// joins). Override to <c>LastPerSubject</c> for an ACCUMULATING projection
    /// (counters/rings) so a fresh consumer does not re-count the whole backlog.</summary>
    protected virtual ConsumerConfigDeliverPolicy DeliverPolicy => ConsumerConfigDeliverPolicy.All;

    /// <summary>Hook to initialise resources (e.g. open a KV store) before consuming.</summary>
    protected virtual ValueTask OnStartAsync(NatsJSContext js, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Project one event. Exceptions are logged and swallowed (message is still acked).</summary>
    protected abstract ValueTask ProcessAsync(string subject, ReadOnlyMemory<byte> data, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await using var nc = new NatsConnection(_opts.BuildNatsOpts());
        var js = new NatsJSContext(nc);
        Log.LogInformation("connecting to NATS at {url}", _opts.Url);

        await OnStartAsync(js, ct);

        var config = new ConsumerConfig
        {
            DurableName = DurableName,
            Name = DurableName,
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = DeliverPolicy,
        };
        // One filter → FilterSubject (singular); several disjoint subtrees → FilterSubjects
        // (plural). JetStream rejects setting both, so exactly one is populated.
        var filters = FilterSubjects is { Count: > 0 } fs ? fs : new[] { FilterSubject };
        if (filters.Count == 1)
            config.FilterSubject = filters[0];
        else
            config.FilterSubjects = filters.ToArray();
        var consumer = await js.CreateOrUpdateConsumerAsync(StreamName, config, ct);
        Ready = true;
        Log.LogInformation("durable consumer {durable} on {stream} ({filter})",
            DurableName, StreamName, string.Join(",", filters));

        // Explicit fetch long-poll loop. `ConsumeAsync` was observed to drain the
        // backlog then silently stop yielding new messages (it blocks without ending,
        // so a retry-on-end loop can't recover it). FetchAsync returns after MaxMsgs
        // or Expires; the outer loop immediately re-fetches, giving reliable
        // continuous consumption + self-healing.
        var fetchOpts = new NatsJSFetchOpts { MaxMsgs = 200, Expires = TimeSpan.FromSeconds(30) };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var msg in consumer.FetchAsync<byte[]>(opts: fetchOpts, cancellationToken: ct))
                {
                    try
                    {
                        await ProcessAsync(msg.Subject, msg.Data ?? Array.Empty<byte>(), ct);
                        EventsProcessed++;
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning(e, "project failed for {subject}", msg.Subject);
                    }
                    finally
                    {
                        await msg.AckAsync(cancellationToken: ct);
                    }
                }
                // batch ended (full or expiry) — loop re-fetches immediately
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Log.LogWarning(e, "fetch loop error — retrying in 5s");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }
}
