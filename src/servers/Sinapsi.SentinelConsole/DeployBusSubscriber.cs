using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using Sinapsi.Nats;

namespace Sinapsi.SentinelConsole;

/// <summary>
/// The bus ingest for the Console's deploy-visibility lane: a core-NATS subscriber on
/// <c>release.&gt;</c> + <c>deploy.&gt;</c> that parses each event into a
/// <see cref="DeployEvent"/> and feeds the <see cref="DeployModel"/>. Mirrors
/// <see cref="SecurityBusSubscriber"/> exactly (separate class because it is a distinct
/// subject family + read-model, not because the mechanics differ). Read-only: it never
/// publishes or acts. Resilient: a connection blip is retried with backoff, never crashing
/// the process.
///
/// NOTE (follow-up, not done here): the Console's NATS identity today is scoped
/// subscribe-only on <c>security.&gt;</c> (see <c>config.env.example</c>).
/// Going live with this subscriber requires
/// widening that identity's subscribe permissions to also include
/// <c>release.&gt;</c> + <c>deploy.&gt;</c> — a separate infrastructure change,
/// out of scope for this PR (code-only). Until that widening lands, this subscriber
/// connects but receives nothing (Ingested stays 0) — it fails safe, not open.
/// </summary>
public sealed class DeployBusSubscriber : BackgroundService
{
    private const string ReleaseSubject = "release.>";
    private const string DeploySubject = "deploy.>";

    private readonly NatsConnectionOptions _opts;
    private readonly DeployModel _model;
    private readonly ILogger<DeployBusSubscriber> _log;

    public bool Connected { get; private set; }
    public long Ingested { get; private set; }

    public DeployBusSubscriber(
        NatsConnectionOptions opts, DeployModel model, ILogger<DeployBusSubscriber> log)
    {
        _opts = opts;
        _model = model;
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
                _log.LogInformation("SentinelConsole subscribed to {S1} + {S2}", ReleaseSubject, DeploySubject);

                await Task.WhenAll(
                    ConsumeAsync(nc, ReleaseSubject, ct),
                    ConsumeAsync(nc, DeploySubject, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Connected = false;
                _log.LogWarning(e, "SentinelConsole deploy-bus subscription dropped; retrying in 5s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        Connected = false;
    }

    private async Task ConsumeAsync(NatsConnection nc, string subject, CancellationToken ct)
    {
        await foreach (var msg in nc.SubscribeAsync<string>(subject, cancellationToken: ct))
        {
            if (msg.Data is not { Length: > 0 } json) continue;
            var e = DeployEvent.TryParse(msg.Subject, json, DateTimeOffset.UtcNow);
            if (e is null) continue;   // not a release/deploy event we recognize — ignore
            _model.Record(e);
            Ingested++;
        }
    }
}
