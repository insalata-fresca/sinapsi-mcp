namespace ApprovalBridge.Executor.Sdk;

/// <summary>
/// The non-secret input to an <see cref="IActionExecutor"/>: the allowlisted <paramref name="ActionId"/>,
/// the canonical <c>param_schema</c>-validated <paramref name="ParamsJson"/>, and the
/// <paramref name="TargetIdentity"/> the executor runs as (the target's own scoped identity — never the
/// requester's, I2). This is derived from the dispatched <see cref="Sinapsi.Nats.EventPlane.ActPayload"/>
/// plus the action definition loaded from the allowlist. It carries no secret.
/// </summary>
/// <param name="ActionId">The allowlisted action id (e.g. <c>garmin.oauth.exchange</c>).</param>
/// <param name="ParamsJson">Canonical, schema-validated, non-secret params JSON.</param>
/// <param name="TargetIdentity">The target's own scoped identity the executor runs under.</param>
public sealed record ExecutorRequest(string ActionId, string ParamsJson, string TargetIdentity);
