using ApprovalBridge.Executor.Sdk;

namespace ApprovalBridge.Executor.Dispatch;

/// <summary>An in-memory registry of pre-deployed handlers, keyed by their <see cref="IActionExecutor.ExecutorName"/>.</summary>
public sealed class InMemoryActionExecutorRegistry : IActionExecutorRegistry
{
    private readonly IReadOnlyDictionary<string, IActionExecutor> _byName;

    public InMemoryActionExecutorRegistry(IEnumerable<IActionExecutor> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var map = new Dictionary<string, IActionExecutor>(StringComparer.Ordinal);
        foreach (var h in handlers)
        {
            if (map.ContainsKey(h.ExecutorName))
                throw new ArgumentException($"duplicate executor handler '{h.ExecutorName}'", nameof(handlers));
            map[h.ExecutorName] = h;
        }
        _byName = map;
    }

    public IActionExecutor? Find(string executorName) =>
        !string.IsNullOrEmpty(executorName) && _byName.TryGetValue(executorName, out var h) ? h : null;
}
