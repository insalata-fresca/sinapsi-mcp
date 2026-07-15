using System.Text.Json.Nodes;
using ApprovalBridge.Executor.Registry;
using ApprovalBridge.Executor.Sdk;
using Json.Schema;
using Sinapsi.Nats.EventPlane;

namespace ApprovalBridge.Executor.Dispatch;

/// <summary>
/// The GENERIC executor SDK core (home-server <c>docs/66 §3.4/§4</c>, E1.4): a real
/// <see cref="IActCommandDispatcher"/> the broker can be wired to <b>when configured live</b>. It receives a
/// dispatched <see cref="ActCommand"/> of kind <see cref="ActCommandKind.ApprovalBridgeExecute"/>, loads the
/// action definition from the E1.1 allowlist, runs the pre-registered scoped action under the target's own
/// identity via the bound <see cref="IActionExecutor"/>, and returns ONLY a result conforming to the action's
/// <c>result_schema</c> — the secret is read target-side by the handler and never touches the broker/agent (I2).
///
/// <para>It is action-agnostic (I1): nothing here is Garmin-specific. The demo <c>garmin.oauth.exchange</c>
/// handler is just one registered <see cref="IActionExecutor"/>. Every failure is deny-by-default — an
/// unknown action, malformed params, a missing handler, a schema-violating result, or a thrown
/// <see cref="ExecutorException"/> all yield a REJECTED ack with a non-secret reason, never a leak.</para>
/// </summary>
public sealed class ExecutorDispatcher : IActCommandDispatcher
{
    private readonly IActionDefinitionSource _definitions;
    private readonly IActionExecutorRegistry _handlers;
    private readonly ISecretSourceFactory _secretSources;

    public ExecutorDispatcher(
        IActionDefinitionSource definitions,
        IActionExecutorRegistry handlers,
        ISecretSourceFactory secretSources)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _secretSources = secretSources ?? throw new ArgumentNullException(nameof(secretSources));
    }

    public async ValueTask<ActCommandAck> DispatchAsync(ActCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Only this bridge's execute kind is handled — a merge/deploy command is not ours to run.
        if (command.Kind != ActCommandKind.ApprovalBridgeExecute)
            return ActCommandAck.Reject($"executor handles only {nameof(ActCommandKind.ApprovalBridgeExecute)}, not {command.Kind}");

        // Subject discipline (C2 invariant): an act is a command on the act-command tree, never a fact trigger.
        EventPlaneChannels.EnsureNotFactTriggered(command.Subject);

        // The dispatch must carry the non-secret action payload (action_id + validated params).
        if (command.Payload is not { } payload || string.IsNullOrEmpty(payload.ActionId))
            return ActCommandAck.Reject("act-command carries no action payload (action_id + params)");

        // Load the action definition from the allowlist — an unregistered action_id is the deny floor (I6).
        var def = _definitions.Find(payload.ActionId);
        if (def is null)
            return ActCommandAck.Reject($"action '{payload.ActionId}' is not in the executor allowlist");

        // Re-validate params against param_schema, target-side (defense-in-depth; the broker already did too).
        if (!ValidateAgainst(def.ParamSchema, payload.ParamsJson))
            return ActCommandAck.Reject($"params for '{payload.ActionId}' violate param_schema");

        // Resolve the pre-deployed handler bound to this action's executor name.
        var handler = _handlers.Find(def.ExecutorName);
        if (handler is null)
            return ActCommandAck.Reject($"no executor handler bound to '{def.ExecutorName}'");

        // Build the Path-D secret source SCOPED TO THE TARGET IDENTITY (I2) — the secret is read only here,
        // target-side, and never returned to the broker.
        var secrets = _secretSources.ForTarget(def);
        var request = new ExecutorRequest(payload.ActionId, payload.ParamsJson, def.TargetIdentity);

        ExecutorResult result;
        try
        {
            result = await handler.ExecuteAsync(request, secrets, ct);
        }
        catch (ExecutorException ex)
        {
            // Benign, non-secret handler failure → deny-by-default with the handler's non-secret reason.
            return ActCommandAck.Reject($"executor '{def.ExecutorName}' failed: {ex.Message}");
        }

        if (result is null || result.ResultJson is null)
            return ActCommandAck.Reject($"executor '{def.ExecutorName}' returned no result");

        // The result MUST conform to result_schema — a closed, non-secret shape. Anything else is refused,
        // so a mis-authored handler cannot exfiltrate a token/secret through the result surface (I2).
        if (!ValidateAgainst(def.ResultSchema, result.ResultJson))
            return ActCommandAck.Reject($"result for '{payload.ActionId}' violates result_schema (refused before return)");

        // Belt-and-suspenders on the seal: reject ANY result key not declared in result_schema.properties,
        // even if an open `additionalProperties` would let the schema pass. The result surface is a strict
        // whitelist — an undeclared field (a smuggled token) fails closed and never reaches the broker.
        if (!ResultKeysAreDeclared(result.ResultJson, def.ResultProperties, out var offendingKey))
            return ActCommandAck.Reject($"result for '{payload.ActionId}' carries undeclared field '{offendingKey}' (refused before return)");

        // Accept and carry back ONLY the validated, non-secret result.
        return ActCommandAck.AcceptWithResult(
            result.ResultJson,
            reason: $"executed '{payload.ActionId}' under target identity '{def.TargetIdentity}'");
    }

    private static bool ValidateAgainst(JsonSchema schema, string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (System.Text.Json.JsonException) { return false; }
        return schema.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid;
    }

    // Every top-level key of the result object must be a declared result_schema property. Fails closed on a
    // non-object result or any undeclared key (the smuggled-field defence).
    private static bool ResultKeysAreDeclared(string json, IReadOnlySet<string> declared, out string offendingKey)
    {
        offendingKey = string.Empty;
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { offendingKey = "<non-json>"; return false; }
        if (node is not JsonObject obj) { offendingKey = "<non-object>"; return false; }
        foreach (var kv in obj)
        {
            if (!declared.Contains(kv.Key)) { offendingKey = kv.Key; return false; }
        }
        return true;
    }
}
