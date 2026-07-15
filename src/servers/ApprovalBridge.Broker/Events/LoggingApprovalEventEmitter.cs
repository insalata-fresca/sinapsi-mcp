using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sinapsi.Nats.EventPlane;

namespace ApprovalBridge.Broker.Events;

/// <summary>Shadow-mode emitter: logs the classified bridge fact instead of publishing to NATS, and
/// still honours the DLQ contract for unclassifiable verdicts (deny-by-default). Used when the broker
/// is deployed dormant with no bus wired — it lets the full chain be exercised locally without emitting
/// onto the live event fabric.</summary>
internal sealed class LoggingApprovalEventEmitter : IApprovalEventEmitter
{
    private readonly ILogger<LoggingApprovalEventEmitter> _log;
    private readonly IDeadLetterSink _dlq;

    public LoggingApprovalEventEmitter(ILogger<LoggingApprovalEventEmitter> log, IDeadLetterSink dlq)
    {
        _log = log;
        _dlq = dlq;
    }

    public async ValueTask EmitAsync(ApprovalFact fact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!BridgeEnvelope.IsClassifiable(fact.Verdict))
        {
            var outcome = await DeadLetterRouter.RouteAsync(
                _dlq, fact.CorrelationId, $"unclassifiable approval verdict '{fact.Verdict}'",
                UnclassifiedFallback.Deny, ct);
            _log.LogWarning("bridge fact dead-lettered: {Subject} -> {Verdict}", outcome.DlqSubject, outcome.Verdict);
            return;
        }
        _log.LogInformation("bridge fact (shadow, not published): {Subject} {Data}",
            BridgeEnvelope.SubjectFor(fact.ActionId, fact.Verdict), fact.Envelope.ToJsonString());
    }
}

/// <summary>Shadow DLQ sink: records dead-letters to the log. A real sink (durable NATS publish) is
/// wired only once the bus is provisioned.</summary>
internal sealed class LoggingDeadLetterSink : IDeadLetterSink
{
    private readonly ILogger<LoggingDeadLetterSink> _log;
    public LoggingDeadLetterSink(ILogger<LoggingDeadLetterSink> log) => _log = log;

    public ValueTask WriteAsync(DeadLetterOutcome outcome, string changeRef, CancellationToken ct = default)
    {
        _log.LogWarning("DLQ {Subject} verdict={Verdict} ref={Ref} reason={Reason}",
            outcome.DlqSubject, outcome.Verdict, changeRef, outcome.Reason);
        return ValueTask.CompletedTask;
    }
}
