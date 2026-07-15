using System.Text.Json.Nodes;

namespace ApprovalBridge.Broker.Events;

/// <summary>One bridge FACT to emit: the classified verdict, its subject action, the envelope, and the
/// change-ref used for dead-lettering if it cannot be classified.</summary>
/// <param name="ActionId">The action the fact is about (subject token).</param>
/// <param name="Verdict">requested | approved | rejected | executed | expired.</param>
/// <param name="Envelope">The docs/66 §9 <c>data</c> payload.</param>
/// <param name="CorrelationId">== request_id (the DLQ change-ref if unclassifiable).</param>
internal sealed record ApprovalFact(string ActionId, string Verdict, JsonObject Envelope, string CorrelationId);

/// <summary>
/// Emits bridge decision FACTS (docs/66 §9) as CloudEvents on
/// <c>homelab.security.approval.&lt;action_id&gt;.&lt;verdict&gt;</c>. An unclassifiable fact (verdict outside the
/// closed vocabulary) is routed to the C2 <c>DeadLetterRouter</c> with a non-permissive fallback — never
/// silently dropped, never allowed (docs/66 §8, docs/64 §3 DLQ + deny-by-default).
/// </summary>
internal interface IApprovalEventEmitter
{
    /// <summary>Emit one classified fact, or dead-letter it if the verdict is unclassifiable.</summary>
    ValueTask EmitAsync(ApprovalFact fact, CancellationToken ct = default);
}
