using System.Text.Json.Nodes;
using Sinapsi.Nats.EventPlane;

namespace Sinapsi.DeliveryEvaluator;

/// <summary>
/// Emits a <see cref="RiskVerdict"/> into the common decision envelope (home-server
/// <c>docs/61 §8</c>) as a VERDICT FACT — reusing the C2/C3 Published-Language pieces:
/// the shared verdict vocabulary (<see cref="DecisionEnvelopeContract.Verdicts"/>) and the
/// verdict-fact / dead-letter subject discipline (<see cref="EventPlaneChannels"/>).
///
/// <para><b>Fact, never a trigger.</b> A verdict is published pub/sub (many consumers, no single
/// owner) and must NEVER itself fire the merge/deploy — that is a distinct ACT command
/// (<c>docs/64 §3</c>, <see cref="EventPlaneChannels.EnsureNotFactTriggered"/>). This type only
/// produces the fact payload + its subject; it never dispatches an act.</para>
///
/// <para><b>Verdict-vocabulary compatible, layer-extended.</b> The delivery evaluator is a distinct
/// plane from the Q1/Q2/Q3 authorization layers, so it reuses the shared <i>verdict</i> vocabulary
/// and <c>correlation_id</c> (what makes its output directly comparable — <c>docs/65 §1</c>) but
/// occupies a <c>delivery-evaluator</c> layer token outside the closed q1/q2/q3
/// <see cref="DecisionEnvelopeContract.Layers"/> set. That layer-vocab gap is flagged for MC (see
/// the PR body) rather than silently widening the C2 contract.</para>
/// </summary>
public static class DeliveryVerdictEnvelope
{
    /// <summary>The layer token the delivery evaluator stamps (not one of q1/q2/q3).</summary>
    public const string Layer = "delivery-evaluator";

    /// <summary>The surface token for this evaluator.</summary>
    public const string Surface = "delivery-risk-evaluator";

    /// <summary>Build the envelope <c>data</c> payload (docs/61 §8 shape + delivery fields).</summary>
    /// <exception cref="ArgumentOutOfRangeException">the verdict is outside the shared vocabulary —
    /// a defensive guard proving the output is comparable to the authz layers.</exception>
    public static JsonObject ToEnvelopeData(RiskVerdict verdict, string? correlationId = null)
    {
        var token = verdict.Verdict.ToToken();
        if (!DecisionEnvelopeContract.Verdicts.Contains(token))
            throw new ArgumentOutOfRangeException(nameof(verdict),
                $"verdict token '{token}' is outside the shared decision-envelope vocabulary");

        return new JsonObject
        {
            // Shared core (docs/61 §8) — the fields that make it comparable to Q1/Q2/Q3.
            ["layer"] = Layer,
            ["question"] = "change-safety",
            ["surface"] = Surface,
            ["verdict"] = token,
            ["reason"] = verdict.Reason,
            ["correlation_id"] = correlationId ?? "",
            // Delivery-evaluator extension fields (the effect classification the operator sees).
            ["tier"] = verdict.Tier.ToString(),
            ["confidence"] = verdict.Confidence.ToString(),
            ["touched_trust_plane"] = verdict.TouchedTrustPlane,
            ["surfaces"] = new JsonArray(verdict.Surfaces.Select(s => (JsonNode)s.ToString()).ToArray()),
            ["signals"] = new JsonArray(verdict.Signals.Select(s => (JsonNode)s.Code).ToArray()),
        };
    }

    /// <summary>
    /// The subject this decision belongs on. A normal verdict is a FACT under the shared verdict-fact
    /// root (captured by HOMELAB_AUDIT, recognised by <see cref="EventPlaneChannels.IsVerdictFactSubject"/>,
    /// never a trigger). An <see cref="RiskVerdict.Unparseable"/> change is dead-lettered under
    /// <see cref="EventPlaneChannels.DeadLetterSubjectRoot"/> (deny-by-default, written once —
    /// <c>docs/61 §8</c>).
    /// </summary>
    public static string SubjectFor(RiskVerdict verdict)
    {
        if (verdict.Unparseable)
            return $"{EventPlaneChannels.DeadLetterSubjectRoot}.unclassifiable-change";

        var slug = verdict.Verdict switch
        {
            Verdict.Allow => "allow",
            Verdict.RequiresApproval => "requires-approval", // metachar-free slug (docs/61 §8)
            Verdict.Deny => "deny",
            _ => "requires-approval",
        };
        return $"{EventPlaneChannels.VerdictFactSubjectRoot}.{Layer}.{slug}.{Surface}";
    }
}
