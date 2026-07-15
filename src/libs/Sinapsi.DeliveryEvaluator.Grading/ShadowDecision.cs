using System.Text.Json.Nodes;

namespace Sinapsi.DeliveryEvaluator.Grading;

/// <summary>
/// One decision observed on the SHADOW stream: what the evaluator emitted as a verdict FACT while
/// enforcement was OFF (home-server <c>docs/64 §4</c> Track B — "shadow-first before enforce").
///
/// <para>Parsed from the delivery evaluator's decision-envelope <c>data</c> payload (see
/// <see cref="Sinapsi.DeliveryEvaluator.DeliveryVerdictEnvelope.ToEnvelopeData"/>): the shared
/// <c>verdict</c> + <c>correlation_id</c>, plus — when present — the change input needed to RECOMPUTE
/// the would-enforce verdict. The as-built verdict-fact envelope does NOT carry the raw change, so
/// <see cref="DiffSummary"/> is optional; a record without it is <see cref="CanRecompute"/> = false
/// and the stream-diff treats it as an unverifiable gap (fail-safe). Joining the shadow stream to the
/// change by <c>correlation_id</c> is the flagged LIVE-WIRE follow-on.</para>
/// </summary>
/// <param name="CorrelationId">The change's correlation id (join key to the change / PR).</param>
/// <param name="ShadowVerdict">The verdict the evaluator emitted in shadow.</param>
/// <param name="DiffSummary">The change effect text, if carried on the record — enables the
/// would-enforce recompute. Null when only the verdict fact is available.</param>
public sealed record ShadowDecision(string CorrelationId, Verdict ShadowVerdict, string? DiffSummary)
{
    /// <summary>True when the record carries enough to recompute the would-enforce verdict.</summary>
    public bool CanRecompute => !string.IsNullOrWhiteSpace(DiffSummary);

    /// <summary>Parse a shadow decision from a delivery-evaluator envelope <c>data</c> payload.
    /// Reads <c>verdict</c> + <c>correlation_id</c>; picks up the change from an optional
    /// <c>diff_summary</c> extension field if the emitter adds it (the live-wire requirement).</summary>
    /// <exception cref="ArgumentException">the payload has no parseable verdict.</exception>
    public static ShadowDecision FromEnvelopeData(JsonObject data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));

        var verdictToken = Str(data, "verdict")
            ?? throw new ArgumentException("envelope data has no 'verdict' field", nameof(data));
        var verdict = verdictToken switch
        {
            "allow" => Verdict.Allow,
            "requiresApproval" => Verdict.RequiresApproval,
            "deny" => Verdict.Deny,
            _ => throw new ArgumentException(
                $"verdict '{verdictToken}' outside the shared vocabulary [allow, requiresApproval, deny]", nameof(data)),
        };

        return new ShadowDecision(
            CorrelationId: Str(data, "correlation_id") ?? "",
            ShadowVerdict: verdict,
            DiffSummary: Str(data, "diff_summary"));
    }

    private static string? Str(JsonObject data, string field) =>
        data.TryGetPropertyValue(field, out var n) && n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
