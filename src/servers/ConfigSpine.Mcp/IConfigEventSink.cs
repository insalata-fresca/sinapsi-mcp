using System.Text.Json.Nodes;

namespace ConfigSpine.Mcp;

/// <summary>
/// The seam the tool publishes through. Abstracting the NATS publisher behind this interface keeps
/// the tool's subject-validation + publish-shape logic unit-testable without a live bus: a test can
/// substitute a recording fake and assert exactly which subject + data the tool would emit. The
/// production implementation is <see cref="NatsConfigEventSink"/>. Public only because it is a
/// constructor parameter of the public tool type; the production implementation stays internal.
/// </summary>
public interface IConfigEventSink
{
    /// <summary>Publish <paramref name="data"/> on the fully-composed, already-validated config
    /// <paramref name="subject"/> (always inside <c>homelab.config.&gt;</c>). Implementations wrap
    /// the data in a CloudEvent envelope. Throws on a connect/publish failure (sanitized).</summary>
    Task PublishAsync(string subject, JsonObject data, CancellationToken ct);
}
