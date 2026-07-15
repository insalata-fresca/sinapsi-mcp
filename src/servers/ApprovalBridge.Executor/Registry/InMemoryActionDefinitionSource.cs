namespace ApprovalBridge.Executor.Registry;

/// <summary>An in-memory set of executor-side action definitions — the base the YAML loader materialises
/// into, and the fixture the tests use without touching disk.</summary>
public sealed class InMemoryActionDefinitionSource : IActionDefinitionSource
{
    private readonly IReadOnlyDictionary<string, ExecutorActionDefinition> _byId;

    public InMemoryActionDefinitionSource(IEnumerable<ExecutorActionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var map = new Dictionary<string, ExecutorActionDefinition>(StringComparer.Ordinal);
        foreach (var d in definitions)
        {
            if (map.ContainsKey(d.ActionId))
                throw new ArgumentException($"duplicate action_id '{d.ActionId}' in executor definitions", nameof(definitions));
            map[d.ActionId] = d;
        }
        _byId = map;
    }

    public ExecutorActionDefinition? Find(string actionId) =>
        !string.IsNullOrEmpty(actionId) && _byId.TryGetValue(actionId, out var d) ? d : null;
}
