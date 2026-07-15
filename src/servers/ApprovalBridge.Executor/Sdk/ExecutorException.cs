namespace ApprovalBridge.Executor.Sdk;

/// <summary>
/// Raised by a handler for a benign, non-secret failure (e.g. the token endpoint refused the code). The
/// message is treated as operator/audit-visible, so a handler MUST NOT put a secret in it. The
/// <see cref="Dispatch.ExecutorDispatcher"/> converts this into a rejected act-command with a non-secret
/// reason — the deny-by-default posture, never a leak.
/// </summary>
public sealed class ExecutorException(string message) : Exception(message);
