using ApprovalBridge.Executor.Sdk;

namespace ApprovalBridge.Executor.Dispatch;

/// <summary>Resolves the pre-deployed <see cref="IActionExecutor"/> bound to an action definition's
/// <c>executor</c> name. A name with no registered handler is refused (deny-by-default).</summary>
public interface IActionExecutorRegistry
{
    /// <summary>The handler bound to <paramref name="executorName"/>, or null when none is registered.</summary>
    IActionExecutor? Find(string executorName);
}
