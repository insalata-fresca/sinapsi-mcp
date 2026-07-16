using System.Text.Json.Nodes;

namespace DeliveryEvaluator.Host;

/// <summary>
/// The host's ONLY write path to the bus: publish a delivery-risk VERDICT as a CloudEvent FACT.
///
/// <para><b>Observe-only is structural here.</b> This interface can publish a <i>fact</i> and
/// nothing else. There is deliberately NO act-dispatch member — the host holds no
/// <c>IActCommandDispatcher</c>, constructs no <c>ActCommand</c>, and has no code path onto the
/// <c>delivery.command.&gt;</c> act tree. The only subjects that ever reach
/// <see cref="PublishAsync"/> come from <see cref="Sinapsi.DeliveryEvaluator.DeliveryVerdictEnvelope.SubjectFor"/>,
/// which yields only a verdict-fact subject (under <c>homelab.security.authz</c>) or a dead-letter
/// subject (under <c>delivery.dlq</c>) — never an act command. A verdict is a fact, never a trigger
/// (home-server <c>docs/64 §3</c>).</para>
/// </summary>
public interface IVerdictFactPublisher
{
    /// <summary>Publish <paramref name="data"/> as a CloudEvent on the verdict-fact (or dead-letter)
    /// <paramref name="subject"/>.</summary>
    ValueTask PublishAsync(string subject, JsonObject data, CancellationToken ct = default);
}
