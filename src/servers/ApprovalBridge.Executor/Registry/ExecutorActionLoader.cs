using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.RepresentationModel;

namespace ApprovalBridge.Executor.Registry;

/// <summary>
/// Loads executor-side <see cref="ExecutorActionDefinition"/>s from the git-backed allowlist YAML
/// (E1.1 <c>policies/approval-bridge/actions/&lt;action_id&gt;.yaml</c>). This is the target-side twin of
/// the broker's loader: it extracts the fields the executor needs — <c>executor</c>, <c>target.identity</c>,
/// <c>param_schema</c>, and (unlike the broker) <c>result_schema</c>, which the executor uses to prove its
/// result is non-secret. Strict by construction: a missing field, or a <c>param_schema</c> that is not a
/// closed object, is refused (deny-by-default, home-server <c>docs/66 §2 I6</c>).
/// </summary>
public static class ExecutorActionLoader
{
    /// <summary>Load every <c>*.yaml</c> under <paramref name="actionsDir"/> into a definition source.</summary>
    public static InMemoryActionDefinitionSource LoadDirectory(string actionsDir)
    {
        if (!Directory.Exists(actionsDir))
            throw new DirectoryNotFoundException($"action allowlist directory not found: {actionsDir}");
        var defs = new List<ExecutorActionDefinition>();
        foreach (var path in Directory.EnumerateFiles(actionsDir, "*.yaml").OrderBy(p => p, StringComparer.Ordinal))
            defs.Add(ParseFile(path));
        return new InMemoryActionDefinitionSource(defs);
    }

    /// <summary>Parse one allowlist YAML into an <see cref="ExecutorActionDefinition"/>. Exposed for tests.</summary>
    public static ExecutorActionDefinition ParseFile(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var root = LoadRootMapping(File.ReadAllText(path), path);

        var actionId = Str(root, "action_id", path);
        if (!string.Equals(actionId, stem, StringComparison.Ordinal))
            throw new InvalidDataException($"{path}: action_id '{actionId}' must equal filename stem '{stem}'");

        var target = Map(root, "target", path);
        var paramSchemaNode = ToJsonNode(Require(root, "param_schema", path)) as JsonObject
            ?? throw new InvalidDataException($"{path}: param_schema must be a mapping");
        GuardClosedObject(paramSchemaNode, path);

        var resultSchemaNode = ToJsonNode(Require(root, "result_schema", path)) as JsonObject
            ?? throw new InvalidDataException($"{path}: result_schema must be a mapping");
        if (resultSchemaNode["type"]?.GetValue<string>() != "object")
            throw new InvalidDataException($"{path}: result_schema.type must be 'object'");

        var resultProps = new HashSet<string>(StringComparer.Ordinal);
        if (resultSchemaNode["properties"] is JsonObject props)
            foreach (var kv in props) resultProps.Add(kv.Key);
        if (resultProps.Count == 0)
            throw new InvalidDataException($"{path}: result_schema.properties must declare at least one field");

        return new ExecutorActionDefinition(
            ActionId: actionId,
            ExecutorName: Str(root, "executor", path),
            TargetIdentity: Str(target, "identity", path),
            ParamSchema: JsonSchema.FromText(paramSchemaNode.ToJsonString()),
            ResultSchema: JsonSchema.FromText(resultSchemaNode.ToJsonString()),
            ResultProperties: resultProps);
    }

    // param_schema must be closed: object + additionalProperties:false, so params can never widen.
    private static void GuardClosedObject(JsonObject schema, string path)
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

    private static YamlMappingNode Map(YamlMappingNode m, string key, string path) =>
        m.Children.TryGetValue(new YamlScalarNode(key), out var n) && n is YamlMappingNode child
            ? child
            : throw new InvalidDataException($"{path}: missing or non-mapping '{key}'");

    private static YamlNode Require(YamlMappingNode m, string key, string path) =>
        m.Children.TryGetValue(new YamlScalarNode(key), out var n)
            ? n
            : throw new InvalidDataException($"{path}: missing required '{key}'");

    private static string Str(YamlMappingNode m, string key, string path) =>
        Require(m, key, path) is YamlScalarNode s && s.Value is { Length: > 0 } v
            ? v.Trim()
            : throw new InvalidDataException($"{path}: '{key}' must be a non-empty scalar");

    // Type-preserving YAML→JSON so schema keywords land as the JSON types a validator requires.
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
        if (s.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted)
            return JsonValue.Create(v);
        if (v is "null" or "~" or "") return null;
        if (v is "true" or "false") return JsonValue.Create(v == "true");
        if (long.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);
        if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        return JsonValue.Create(v);
    }
}
