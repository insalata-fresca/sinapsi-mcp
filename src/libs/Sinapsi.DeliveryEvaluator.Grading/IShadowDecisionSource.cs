using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sinapsi.DeliveryEvaluator.Grading;

/// <summary>
/// A source of SHADOW decisions for the <see cref="StreamDiffHarness"/>. The framework is decoupled
/// from where the stream comes from so it runs in CI over a captured fixture today and over the live
/// bus once that is wired.
///
/// <para><b>LIVE-WIRE follow-on (flagged, not built here).</b> The production source is a NATS
/// consumer of the delivery evaluator's verdict-fact subject
/// (<c>homelab.security.authz.delivery-evaluator.&gt;</c>, captured by <c>HOMELAB_AUDIT</c>). It is
/// NOT built in B2 because the live bus is not reachable from the sinapsi-mcp CI container, and
/// because the as-built verdict-fact envelope does not yet carry the raw change needed to recompute
/// would-enforce (see <see cref="ShadowDecision.DiffSummary"/>). B2 ships the framework + this
/// interface; the live <c>NatsShadowDecisionSource</c> + the change-join are the follow-on.</para>
/// </summary>
public interface IShadowDecisionSource
{
    /// <summary>The shadow decisions to diff.</summary>
    IEnumerable<ShadowDecision> Read();
}

/// <summary>
/// A shadow-decision source that reads a JSONL capture of delivery-evaluator envelope <c>data</c>
/// payloads — one JSON object per line. This is the CI-runnable stand-in for the live NATS stream:
/// dump the shadow verdict facts to a file and diff them.
/// </summary>
public sealed class JsonlShadowDecisionSource : IShadowDecisionSource
{
    private readonly IReadOnlyList<string> _lines;

    /// <summary>Build from raw JSONL lines (each line an envelope <c>data</c> object).</summary>
    public JsonlShadowDecisionSource(IEnumerable<string> lines) => _lines = lines.ToList();

    /// <summary>Read a JSONL capture from a file path.</summary>
    public static JsonlShadowDecisionSource FromFile(string path) =>
        new(File.ReadAllLines(path));

    /// <inheritdoc />
    public IEnumerable<ShadowDecision> Read()
    {
        foreach (var line in _lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var node = JsonNode.Parse(line) as JsonObject
                ?? throw new JsonException($"shadow-stream line is not a JSON object: {line}");
            yield return ShadowDecision.FromEnvelopeData(node);
        }
    }
}
