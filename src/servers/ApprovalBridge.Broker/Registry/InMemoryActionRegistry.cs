using ApprovalBridge.Broker.Model;

namespace ApprovalBridge.Broker.Registry;

/// <summary>An in-memory registry built from already-parsed <see cref="ActionSpec"/>s. Used by the
/// broker's tests (the security core needs no live allowlist file) and as the base the YAML loader
/// materialises into.</summary>
internal sealed class InMemoryActionRegistry : IActionRegistry
{
    private readonly IReadOnlyDictionary<string, ActionSpec> _byId;

    public InMemoryActionRegistry(IEnumerable<ActionSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        var map = new Dictionary<string, ActionSpec>(StringComparer.Ordinal);
        foreach (var s in specs)
        {
            if (map.ContainsKey(s.ActionId))
                throw new ArgumentException($"duplicate action_id '{s.ActionId}' in registry", nameof(specs));
            map[s.ActionId] = s;
        }
        _byId = map;
    }

    public ActionSpec? Find(string actionId) =>
        !string.IsNullOrEmpty(actionId) && _byId.TryGetValue(actionId, out var s) ? s : null;

    public IReadOnlyCollection<string> ActionIds => (IReadOnlyCollection<string>)_byId.Keys;
}
