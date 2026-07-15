using System.Text.Json.Nodes;
using DeliveryEvaluator.Host;

namespace DeliveryEvaluator.Host.Tests;

/// <summary>A fake <see cref="IVerdictFactPublisher"/> that records every (subject, data) it is
/// asked to publish — so a test can assert exactly which subjects the host writes.</summary>
internal sealed class RecordingPublisher : IVerdictFactPublisher
{
    public List<(string Subject, JsonObject Data)> Published { get; } = new();

    public ValueTask PublishAsync(string subject, JsonObject data, CancellationToken ct = default)
    {
        // Enforce the SAME observe-only guard the real NATS publisher enforces, so the structural
        // property is exercised even without a live bus.
        NatsVerdictFactPublisher.EnsureFactNotAct(subject);
        Published.Add((subject, data));
        return ValueTask.CompletedTask;
    }
}
