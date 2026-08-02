using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using Sinapsi.Nats;

namespace Sinapsi.SentinelConsole;

/// <summary>
/// The bus ingest for the Console: a core-NATS subscriber on
/// <c>security.&gt;</c> that parses each event into an <see cref="AuthzDecision"/>
/// and feeds the <see cref="ReadModel"/> + <see cref="LiveFeed"/>. Read-only: it never
/// publishes or acts — this is the inspection layer. Resilient: a connection blip is
/// retried with backoff, never crashing the process.
/// </summary>
public sealed class SecurityBusSubscriber : BackgroundService
{
    // The security-audit family that carries authorization decisions across layers:
    // Q2 authz.q2.*, Q3 ask-gate.* / deny-floor.* / tier4.* / credential-guard.* / scope_gate.*.
    private const string Subject = "security.>";

    private readonly NatsConnectionOptions _opts;
    private readonly ReadModel _readModel;
    private readonly LiveFeed _feed;
    private readonly ILogger<SecurityBusSubscriber> _log;

    public bool Connected { get; private set; }
    public long Ingested { get; private set; }

    public SecurityBusSubscriber(
        NatsConnectionOptions opts, ReadModel readModel, LiveFeed feed,
        ILogger<SecurityBusSubscriber> log)
    {
        _opts = opts;
        _readModel = readModel;
        _feed = feed;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var nc = new NatsConnection(_opts.BuildNatsOpts());
                await nc.ConnectAsync();
                Connected = true;
                _log.LogInformation("SentinelConsole subscribed to {Subject}", Subject);

                await foreach (var msg in nc.SubscribeAsync<string>(Subject, cancellationToken: ct))
                {
                    if (msg.Data is not { Length: > 0 } json) continue;
                    var d = AuthzDecision.TryParse(msg.Subject, json, DateTimeOffset.UtcNow);
                    if (d is null) continue;      // not an authorization decision — ignore
                    _readModel.Record(d);
                    _feed.Publish(d);
                    Ingested++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Connected = false;
                _log.LogWarning(e, "SentinelConsole bus subscription dropped; retrying in 5s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        Connected = false;
    }
}
