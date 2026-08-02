using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using Sinapsi.Nats;

namespace Sinapsi.SentinelConsole;

/// <summary>
/// The bus ingest for the Console's Operator Approval Bridge lane: a core-NATS subscriber on
/// <c>security.approval.&gt;</c> that parses each event into an <see cref="ApprovalEvent"/> and
/// feeds the <see cref="ApprovalQueueModel"/> lifecycle feed (requirement 3: reflect the
/// request/approved/rejected/executed lifecycle, correlation-id joined). Mirrors
/// <see cref="SecurityBusSubscriber"/> / <see cref="DeployBusSubscriber"/> exactly (a separate class
/// because this is a distinct subject family + read-model, not because the mechanics differ). Read-only:
/// it never publishes or acts — the Console's Approve/Reject buttons go through
/// <see cref="BrokerClient"/> to the broker's own command API, never through the bus. Resilient: a
/// connection blip is retried with backoff, never crashing the process.
///
/// <para><c>security.approval.&gt;</c> is a subject FAMILY under the same
/// <c>security.&gt;</c> domain <see cref="SecurityBusSubscriber"/> already subscribes to — this
/// is a separate subscriber (rather than widening <see cref="AuthzDecision"/> to parse a fourth "bridge"
/// layer) because the bridge envelope's fields (action_id/requester/approver/params_digest/result_status)
/// are genuinely different from the Q1/Q2/Q3 authz-decision shape, and mixing them would blur two
/// distinct read-models into one ambiguous one.</para>
/// </summary>
public sealed class ApprovalBusSubscriber : BackgroundService
{
    private const string Subject = "security.approval.>";

    private readonly NatsConnectionOptions _opts;
    private readonly ApprovalQueueModel _model;
    private readonly ILogger<ApprovalBusSubscriber> _log;

    public bool Connected { get; private set; }
    public long Ingested { get; private set; }

    public ApprovalBusSubscriber(
        NatsConnectionOptions opts, ApprovalQueueModel model, ILogger<ApprovalBusSubscriber> log)
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
                _log.LogInformation("SentinelConsole subscribed to {Subject}", Subject);

                await foreach (var msg in nc.SubscribeAsync<string>(Subject, cancellationToken: ct))
                {
                    if (msg.Data is not { Length: > 0 } json) continue;
                    var e = ApprovalEvent.TryParse(msg.Subject, json, DateTimeOffset.UtcNow);
                    if (e is null) continue;      // not a recognized bridge lifecycle event — ignore
                    _model.Record(e);
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
                _log.LogWarning(e, "SentinelConsole approval-bus subscription dropped; retrying in 5s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        Connected = false;
    }
}
