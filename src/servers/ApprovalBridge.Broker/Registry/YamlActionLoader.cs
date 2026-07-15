using System.Globalization;
using System.Text.Json.Nodes;
using ApprovalBridge.Broker.Model;
using Json.Schema;
using YamlDotNet.RepresentationModel;

namespace ApprovalBridge.Broker.Registry;

/// <summary>
/// Materialises an <see cref="InMemoryActionRegistry"/> from the git-backed allowlist YAML
/// (E1.1 <c>policies/approval-bridge/actions/&lt;action_id&gt;.yaml</c>). The broker consumes the
/// registry read-only; authoring/validating the registry is home-server's <c>apply.py</c> CI gate,
/// not the broker's job. This loader is deliberately strict: a spec missing a required field, or a
/// <c>param_schema</c> that is not <c>type: object</c> with <c>additionalProperties: false</c>, is
/// refused (deny-by-default — params can never be silently widened, docs/66 §2 I6).
/// </summary>
internal static class YamlActionLoader
{
    /// <summary>Load every <c>*.yaml</c> under <paramref name="actionsDir"/> into a registry.</summary>
    public static InMemoryActionRegistry LoadDirectory(string actionsDir)
    {
        if (!Directory.Exists(actionsDir))
            throw new DirectoryNotFoundException($"action allowlist directory not found: {actionsDir}");
        var specs = new List<ActionSpec>();
        foreach (var path in Directory.EnumerateFiles(actionsDir, "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
            specs.Add(ParseFile(path));
        return new InMemoryActionRegistry(specs);
    }

    /// <summary>Parse one action-entry YAML into an <see cref="ActionSpec"/>. Exposed for tests.</summary>
    public static ActionSpec ParseFile(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var root = LoadRootMapping(File.ReadAllText(path), path);
        var actionId = Str(root, "action_id");
        if (!string.Equals(actionId, stem, StringComparison.Ordinal))
            throw new InvalidDataException($"{path}: action_id '{actionId}' must equal filename stem '{stem}'");

        var target = Map(root, "target");
        var paramSchemaNode = ToJsonNode(Require(root, "param_schema", path)) as JsonObject
            ?? throw new InvalidDataException($"{path}: param_schema must be a mapping");
        GuardParamSchema(paramSchemaNode, path);

        var rate = Map(root, "rate_limit");
        var oneShot = Bool(root, "one_shot");
        if (!oneShot)
            throw new InvalidDataException($"{path}: one_shot must be true (v1 invariant — no standing grants)");

        return new ActionSpec(
            ActionId: actionId,
            Title: Str(root, "title"),
            Description: Str(root, "description"),
            TargetHost: Str(target, "host"),
            TargetIdentity: Str(target, "identity"),
            Executor: Str(root, "executor"),
            ParamSchema: JsonSchema.FromText(paramSchemaNode.ToJsonString()),
            RiskTier: Str(root, "risk_tier"),
            ExpirySeconds: Int(root, "expiry_seconds"),
            RateLimit: new RateLimit(Int(rate, "per_agent_per_hour"), Int(rate, "per_action_per_hour")),
            OneShot: true);
    }

    // param_schema must be closed: object + additionalProperties:false, so params can never widen.
    private static void GuardParamSchema(JsonObject schema, string path)
    {
        if (schema["type"]?.GetValue<string>() != "object")
            throw new InvalidDataException($"{path}: param_schema.type must be 'object'");
        if (schema["additionalProperties"] is not JsonValue av || av.GetValue<bool>() != false)
            throw new InvalidDataException($"{path}: param_schema.additionalProperties must be false (deny-by-default)");
    }

    private static YamlMappingNode LoadRootMapping(string yaml, string path)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode m)
            throw new InvalidDataException($"{path}: expected a top-level YAML mapping");
        return m;
    }

    private static YamlMappingNode Map(YamlMappingNode m, string key) =>
        m.Children.TryGetValue(new YamlScalarNode(key), out var n) && n is YamlMappingNode child
            ? child
            : throw new InvalidDataException($"missing or non-mapping '{key}'");

    private static YamlNode Require(YamlMappingNode m, string key, string path) =>
        m.Children.TryGetValue(new YamlScalarNode(key), out var n)
            ? n
            : throw new InvalidDataException($"{path}: missing required '{key}'");

    private static string Str(YamlMappingNode m, string key) =>
        Require(m, key, "<spec>") is YamlScalarNode s && s.Value is { Length: > 0 } v
            ? v.Trim()
            : throw new InvalidDataException($"'{key}' must be a non-empty scalar");

    private static int Int(YamlMappingNode m, string key) =>
        Require(m, key, "<spec>") is YamlScalarNode s && int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidDataException($"'{key}' must be an integer");

    private static bool Bool(YamlMappingNode m, string key) =>
        Require(m, key, "<spec>") is YamlScalarNode s && bool.TryParse(s.Value, out var v) && v;

    // Type-preserving YAML→JSON so param_schema keywords (minLength, additionalProperties, …)
    // land as the JSON types a JSON-Schema validator requires, not as strings.
    internal static JsonNode? ToJsonNode(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                var obj = new JsonObject();
                foreach (var kv in map.Children)
                    obj[((YamlScalarNode)kv.Key).Value!] = ToJsonNode(kv.Value);
                return obj;
            case YamlSequenceNode seq:
                var arr = new JsonArray();
                foreach (var item in seq.Children) arr.Add(ToJsonNode(item));
                return arr;
            case YamlScalarNode scalar:
                return ScalarToJson(scalar);
            default:
                return null;
        }
    }

    private static JsonNode? ScalarToJson(YamlScalarNode s)
    {
        var v = s.Value ?? string.Empty;
        // A quoted scalar is always a string (YAML plain style is the only one we type-infer).
        if (s.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted)
            return JsonValue.Create(v);
        if (v is "null" or "~" or "") return null;
        if (v is "true" or "false") return JsonValue.Create(v == "true");
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }
}
