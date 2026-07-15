using System.Text.Json.Nodes;

namespace Sinapsi.Nats.EventPlane;

/// <summary>
/// The machine-readable form of the common decision envelope as a PUBLISHED LANGUAGE
/// (home-server <c>docs/64 §3</c> / <c>docs/61 §8</c>) — NOT a Shared Kernel.
///
/// <para><b>Published Language, not Shared Kernel.</b> Each authorization layer (Q1 gateway PEP,
/// Q2 in-MCP authorizer, Q3 harness) builds its OWN envelope with its own serialization code and
/// its own copy of the schema — they do not share a DTO/type. This class is a <i>conformance
/// checker</i> for that shared contract, not the wire type any producer must instantiate. A
/// producer may adopt or ignore it; consumers and tests use it to prove an independently-built
/// envelope conforms. The canonical schema of record is
/// <c>ste/home-server:policies/authz/decision-envelope.v1.schema.json</c>; the constants below
/// are this repo's independent restatement of it (that duplication is the point of a Published
/// Language).</para>
///
/// <para>Validation is over the CloudEvent's <c>data</c> payload (the decision), and it is
/// permissive about EXTRA fields (Q1 adds <c>agent</c>; a layer may add more): only the shared
/// CORE — <see cref="RequiredFields"/> with the closed <see cref="Layers"/>/<see cref="Questions"/>/
/// <see cref="Surfaces"/>/<see cref="Verdicts"/> vocabularies — is enforced.</para>
/// </summary>
public static class DecisionEnvelopeContract
{
    /// <summary>Schema major version this checker enforces.</summary>
    public const string SchemaVersion = "1";

    /// <summary>Path to the canonical schema of record, in the home-server (PAP) repo.</summary>
    public const string CanonicalSchemaPath = "policies/authz/decision-envelope.v1.schema.json";

    /// <summary>The shared core fields every layer must emit in the envelope <c>data</c>.</summary>
    public static readonly IReadOnlyList<string> RequiredFields = new[]
    {
        "layer", "question", "surface", "tool", "server", "verb", "verdict", "reason", "command", "correlation_id",
    };

    /// <summary>Closed vocabulary for <c>layer</c>.</summary>
    public static readonly IReadOnlyCollection<string> Layers = new HashSet<string>(StringComparer.Ordinal) { "q1", "q2", "q3" };

    /// <summary>Closed vocabulary for <c>question</c>.</summary>
    public static readonly IReadOnlyCollection<string> Questions =
        new HashSet<string>(StringComparer.Ordinal) { "identity-tool", "command-safety", "operator-gate" };

    /// <summary>Closed vocabulary for <c>surface</c>.</summary>
    public static readonly IReadOnlyCollection<string> Surfaces =
        new HashSet<string>(StringComparer.Ordinal) { "gateway-pep", "in-mcp-authorizer", "harness" };

    /// <summary>The shared verdict vocabulary (home-server <c>docs/61 §8</c>).</summary>
    public static readonly IReadOnlyCollection<string> Verdicts =
        new HashSet<string>(StringComparer.Ordinal) { "allow", "requiresApproval", "deny" };

    /// <summary>Validate an envelope <c>data</c> payload against the Published Language. Returns the
    /// list of conformance errors — EMPTY means conformant. String fields may be empty
    /// (<c>server</c>/<c>verb</c>/<c>command</c>/<c>correlation_id</c> legitimately are for some
    /// layers); the enum-valued fields may not.</summary>
    public static IReadOnlyList<string> Validate(JsonObject? data)
    {
        var errors = new List<string>();
        if (data is null) { errors.Add("envelope data is null"); return errors; }

        foreach (var f in RequiredFields)
        {
            if (!data.TryGetPropertyValue(f, out var node) || node is null)
            { errors.Add($"missing required field '{f}'"); continue; }
            if (!TryString(node, out _))
                errors.Add($"field '{f}' must be a JSON string");
        }

        CheckEnum(data, "layer", Layers, errors);
        CheckEnum(data, "question", Questions, errors);
        CheckEnum(data, "surface", Surfaces, errors);
        CheckEnum(data, "verdict", Verdicts, errors);

        return errors;
    }

    /// <summary>True when <paramref name="data"/> conforms; <paramref name="errors"/> lists why not.</summary>
    public static bool IsConformant(JsonObject? data, out IReadOnlyList<string> errors)
    {
        errors = Validate(data);
        return errors.Count == 0;
    }

    private static void CheckEnum(JsonObject data, string field, IReadOnlyCollection<string> allowed, List<string> errors)
    {
        if (data.TryGetPropertyValue(field, out var node) && node is not null && TryString(node, out var v)
            && !allowed.Contains(v))
            errors.Add($"field '{field}' has value '{v}' outside the allowed vocabulary [{string.Join(", ", allowed)}]");
    }

    private static bool TryString(JsonNode node, out string value)
    {
        if (node is JsonValue jv && jv.TryGetValue(out string? s)) { value = s; return true; }
        value = string.Empty;
        return false;
    }
}
