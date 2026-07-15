namespace ApprovalBridge.Executor.Sdk;

/// <summary>The status of an executor run — the <c>status</c> enum every bridge <c>result_schema</c>
/// carries (home-server <c>docs/66 §2</c>).</summary>
public enum ExecutorStatus
{
    /// <summary>The action completed; <see cref="ExecutorResult.ResultJson"/> is the non-secret confirmation.</summary>
    Ok,
    /// <summary>The action failed; the result still carries only non-secret error metadata.</summary>
    Error,
}

/// <summary>
/// The ONLY thing an <see cref="IActionExecutor"/> returns: a NON-SECRET result JSON conforming to the
/// action's <c>result_schema</c>. By contract it must never contain a secret, a token, or any privileged
/// material — the executor reads its secret target-side and returns only a confirmation (I2, the seal).
/// The <see cref="Dispatch.ExecutorDispatcher"/> re-validates this against the loaded <c>result_schema</c>
/// and refuses (deny-by-default) anything that does not conform, so a mis-authored handler cannot leak.
/// </summary>
/// <param name="Status">Ok or Error.</param>
/// <param name="ResultJson">Non-secret JSON conforming to <c>result_schema</c> (e.g. <c>{"status":"ok",
/// "stored":true,"expires_at":"…"}</c>).</param>
public sealed record ExecutorResult(ExecutorStatus Status, string ResultJson)
{
    public bool IsOk => Status == ExecutorStatus.Ok;

    public static ExecutorResult Ok(string resultJson) => new(ExecutorStatus.Ok, resultJson);
    public static ExecutorResult Error(string resultJson) => new(ExecutorStatus.Error, resultJson);
}
