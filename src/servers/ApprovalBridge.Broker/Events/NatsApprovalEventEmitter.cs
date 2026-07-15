using Sinapsi.Nats;
using Sinapsi.Nats.EventPlane;

namespace ApprovalBridge.Broker.Events;

/// <summary>
/// Production emitter: publishes classifiable bridge facts as CloudEvents via the C2
/// <see cref="NatsEventPublisher"/>, and routes anything unclassifiable through the C2
/// <see cref="DeadLetterRouter"/> (deny-by-default fallback, written exactly once). This is the reuse
/// of the merged event-plane contracts — the broker adds no new envelope/DLQ machinery.
/// </summary>
internal sealed class NatsApprovalEventEmitter : IApprovalEventEmitter
{
    private readonly NatsEventPublisher _publisher;
    private readonly IDeadLetterSink _dlq;

    public NatsApprovalEventEmitter(NatsEventPublisher publisher, IDeadLetterSink dlq)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _dlq = dlq ?? throw new ArgumentNullException(nameof(dlq));
    }

    public async ValueTask EmitAsync(ApprovalFact fact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!BridgeEnvelope.IsClassifiable(fact.Verdict))
        {
            // Unclassifiable verdict → DLQ + deny-by-default (never emit an ambiguous approval fact).
            await DeadLetterRouter.RouteAsync(
                _dlq, fact.CorrelationId, $"unclassifiable approval verdict '{fact.Verdict}'",
                UnclassifiedFallback.Deny, ct);
            return;
        }
        var subject = BridgeEnvelope.SubjectFor(fact.ActionId, fact.Verdict);
        await _publisher.PublishAsync(subject, fact.Envelope, subjectAttr: fact.CorrelationId, ct);
    }
}
